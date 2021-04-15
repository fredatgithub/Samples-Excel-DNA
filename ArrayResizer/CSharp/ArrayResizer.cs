using System;
using System.Collections.Generic;
using ExcelDna.Integration;

namespace AsyncFunctions
{
    // This class defines a few test functions that can be used to explore the automatic array resizing.
    public static class ResizeTestFunctions
    {
        // Just returns an array of the given size
        public static object[,] dnaMakeArray(int rows, int columns)
        {
            object[,] result = new object[rows, columns];
            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < columns; j++)
                {
                    result[i, j] = i + j;
                }
            }

            return result;
        }

        public static double[,] dnaMakeArrayDoubles(int rows, int columns)
        {
            double[,] result = new double[rows, columns];
            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < columns; j++)
                {
                    result[i, j] = i + (j / 1000.0);
                }
            }

            return result;
        }

        public static object dnaMakeMixedArrayAndResize(int rows, int columns)
        {
            object[,] result = new object[rows, columns];
            for (int j = 0; j < columns; j++)
            {
                result[0, j] = "Col " + j;
            }
            for (int i = 1; i < rows; i++)
            {
                for (int j = 0; j < columns; j++)
                {
                    result[i, j] = i + (j * 0.1);
                }
            }

            return ArrayResizer.dnaResize(result);
        }

        // Makes an array, but automatically resizes the result
        public static object dnaMakeArrayAndResize(int rows, int columns, string unused, string unusedtoo)
        {
            object[,] result = dnaMakeArray(rows, columns);
            return ArrayResizer.dnaResize(result);

            // Can also call Resize via Excel - so if the Resize add-in is not part of this code, it should still work
            // (though calling direct is better for large arrays - it prevents extra marshaling).
            // return XlCall.Excel(XlCall.xlUDF, "Resize", result);
        }

        public static double[,] dnaMakeArrayAndResizeDoubles(int rows, int columns)
        {
            double[,] result = dnaMakeArrayDoubles(rows, columns);
            return ArrayResizer.dnaResizeDoubles(result);
        }
    }

    public class ArrayResizer
    {
        // This function will run in the UDF context.
        // Needs extra protection to allow multithreaded use.
        public static object dnaResize(object[,] array)
        {
            var caller = XlCall.Excel(XlCall.xlfCaller) as ExcelReference;
            if (caller == null)
            {
                return array;
            }

            int rows = array.GetLength(0);
            int columns = array.GetLength(1);

            if (rows == 0 || columns == 0)
                return array;

            // For dynamic-array aware Excel we don't do anything if the caller is a single cell
            // Excel will expand in this case
            if (UtilityFunctions.dnaSupportsDynamicArrays() &&
                caller.RowFirst == caller.RowLast &&
                caller.ColumnFirst == caller.ColumnLast)
            {
                return array;
            }

            if ((caller.RowLast - caller.RowFirst + 1 == rows) &&
                (caller.ColumnLast - caller.ColumnFirst + 1 == columns))
            {
                // Size is already OK - just return result
                return array;
            }

            var rowLast = caller.RowFirst + rows - 1;
            var columnLast = caller.ColumnFirst + columns - 1;

            // Check for the sheet limits
            if (rowLast > ExcelDnaUtil.ExcelLimits.MaxRows - 1 ||
                columnLast > ExcelDnaUtil.ExcelLimits.MaxColumns - 1)
            {
                // Can't resize - goes beyond the end of the sheet - just return #VALUE
                // (Can't give message here, or change cells)
                return ExcelError.ExcelErrorValue;
            }

            // TODO: Add some kind of guard for ever-changing result?
            ExcelAsyncUtil.QueueAsMacro(() =>
            {
                // Create a reference of the right size
                var target = new ExcelReference(caller.RowFirst, rowLast, caller.ColumnFirst, columnLast, caller.SheetId);
                DoResize(target); // Will trigger a recalc by writing formula
            });
            // Return the whole array even if we plan to resize - to prevent flashing #N/A
            return array;
        }

        public static double[,] dnaResizeDoubles(double[,] array)
        {
            var caller = XlCall.Excel(XlCall.xlfCaller) as ExcelReference;
            if (caller == null)
            {
                return array;
            }

            int rows = array.GetLength(0);
            int columns = array.GetLength(1);

            if (rows == 0 || columns == 0)
            {
                return array;
            }

            // For dynamic-array aware Excel we don't do anything if the caller is a single cell
            // Excel will expand in this case
            if (UtilityFunctions.dnaSupportsDynamicArrays() &&
                caller.RowFirst == caller.RowLast &&
                caller.ColumnFirst == caller.ColumnLast)
            {
                return array;
            }

            if ((caller.RowLast - caller.RowFirst + 1 == rows) &&
                (caller.ColumnLast - caller.ColumnFirst + 1 == columns))
            {
                // Size is already OK - just return result
                return array;
            }

            var rowLast = caller.RowFirst + rows - 1;
            var columnLast = caller.ColumnFirst + columns - 1;

            if (rowLast > ExcelDnaUtil.ExcelLimits.MaxRows - 1 ||
                columnLast > ExcelDnaUtil.ExcelLimits.MaxColumns - 1)
            {
                // Can't resize - goes beyond the end of the sheet - just return null (for #NUM!)
                // (Can't give message here, or change cells)
                return null;
            }

            // TODO: Add guard for ever-changing result?
            ExcelAsyncUtil.QueueAsMacro(() =>
            {
                // Create a reference of the right size
                var target = new ExcelReference(caller.RowFirst, rowLast, caller.ColumnFirst, columnLast, caller.SheetId);
                DoResize(target); // Will trigger a recalc by writing formula
            });

            // Return what we have - to prevent flashing #N/A
            return array;
        }

        static void DoResize(ExcelReference target)
        {
            // Get the current state for reset later
            using (new ExcelEchoOffHelper())
            using (new ExcelCalculationManualHelper())
            {
                ExcelReference firstCell = new ExcelReference(target.RowFirst, target.RowFirst, target.ColumnFirst, target.ColumnFirst, target.SheetId);

                // Get the formula in the first cell of the target
                string formula = (string)XlCall.Excel(XlCall.xlfGetCell, 41, firstCell);
                bool isFormulaArray = (bool)XlCall.Excel(XlCall.xlfGetCell, 49, firstCell);
                if (isFormulaArray)
                {
                    // Select the sheet and firstCell - needed because we want to use SelectSpecial.
                    using (new ExcelSelectionHelper(firstCell))
                    {
                        // Extend the selection to the whole array and clear
                        XlCall.Excel(XlCall.xlcSelectSpecial, 6);
                        ExcelReference oldArray = (ExcelReference)XlCall.Excel(XlCall.xlfSelection);

                        oldArray.SetValue(ExcelEmpty.Value);
                    }
                }
                // Get the formula and convert to R1C1 mode
                bool isR1C1Mode = (bool)XlCall.Excel(XlCall.xlfGetWorkspace, 4);
                string formulaR1C1 = formula;
                if (!isR1C1Mode)
                {
                    object formulaR1C1Obj;
                    XlCall.XlReturn formulaR1C1Return = XlCall.TryExcel(XlCall.xlfFormulaConvert, out formulaR1C1Obj, formula, true, false, ExcelMissing.Value, firstCell);
                    if (formulaR1C1Return != XlCall.XlReturn.XlReturnSuccess || formulaR1C1Obj is ExcelError)
                    {
                        string firstCellAddress = (string)XlCall.Excel(XlCall.xlfReftext, firstCell, true);
                        XlCall.Excel(XlCall.xlcAlert, "Cannot resize array formula at " + firstCellAddress + " - formula might be too long when converted to R1C1 format.");
                        firstCell.SetValue("'" + formula);
                        return;
                    }
                    formulaR1C1 = (string)formulaR1C1Obj;
                }
                // Must be R1C1-style references
                object ignoredResult;
                //Debug.Print("Resizing START: " + target.RowLast);
                XlCall.XlReturn formulaArrayReturn = XlCall.TryExcel(XlCall.xlcFormulaArray, out ignoredResult, formulaR1C1, target);
                //Debug.Print("Resizing FINISH");

                // TODO: Find some dummy macro to clear the undo stack

                if (formulaArrayReturn != XlCall.XlReturn.XlReturnSuccess)
                {
                    string firstCellAddress = (string)XlCall.Excel(XlCall.xlfReftext, firstCell, true);
                    XlCall.Excel(XlCall.xlcAlert, "Cannot resize array formula at " + firstCellAddress + " - result might overlap another array.");
                    // Might have failed due to array in the way.
                    firstCell.SetValue("'" + formula);
                }
            }
        }
    }

    // RIIA-style helpers to deal with Excel selections    
    // Don't use if you agree with Eric Lippert here: http://stackoverflow.com/a/1757344/44264
    public class ExcelEchoOffHelper : XlCall, IDisposable
    {
        object oldEcho;

        public ExcelEchoOffHelper()
        {
            oldEcho = XlCall.Excel(XlCall.xlfGetWorkspace, 40);
            XlCall.Excel(XlCall.xlcEcho, false);
        }

        public void Dispose()
        {
            XlCall.Excel(XlCall.xlcEcho, oldEcho);
        }
    }

    public class ExcelCalculationManualHelper : XlCall, IDisposable
    {
        object oldCalculationMode;

        public ExcelCalculationManualHelper()
        {
            oldCalculationMode = XlCall.Excel(XlCall.xlfGetDocument, 14);
            XlCall.Excel(XlCall.xlcOptionsCalculation, 3);
        }

        public void Dispose()
        {
            XlCall.Excel(XlCall.xlcOptionsCalculation, oldCalculationMode);
        }
    }

    // Select an ExcelReference (perhaps on another sheet) allowing changes to be made there.
    // On clean-up, resets all the selections and the active sheet.
    // Should not be used if the work you are going to do will switch sheets, amke new sheets etc.
    public class ExcelSelectionHelper : XlCall, IDisposable
    {
        object oldSelectionOnActiveSheet;
        object oldActiveCellOnActiveSheet;

        object oldSelectionOnRefSheet;
        object oldActiveCellOnRefSheet;

        public ExcelSelectionHelper(ExcelReference refToSelect)
        {
            // Remember old selection state on the active sheet
            oldSelectionOnActiveSheet = XlCall.Excel(XlCall.xlfSelection);
            oldActiveCellOnActiveSheet = XlCall.Excel(XlCall.xlfActiveCell);

            // Switch to the sheet we want to select
            string refSheet = (string)XlCall.Excel(XlCall.xlSheetNm, refToSelect);
            XlCall.Excel(XlCall.xlcWorkbookSelect, new object[] { refSheet });

            // record selection and active cell on the sheet we want to select
            oldSelectionOnRefSheet = XlCall.Excel(XlCall.xlfSelection);
            oldActiveCellOnRefSheet = XlCall.Excel(XlCall.xlfActiveCell);

            // make the selection
            XlCall.Excel(XlCall.xlcFormulaGoto, refToSelect);
        }

        public void Dispose()
        {
            // Reset the selection on the target sheet
            XlCall.Excel(XlCall.xlcSelect, oldSelectionOnRefSheet, oldActiveCellOnRefSheet);

            // Reset the sheet originally selected
            string oldActiveSheet = (string)XlCall.Excel(XlCall.xlSheetNm, oldSelectionOnActiveSheet);
            XlCall.Excel(XlCall.xlcWorkbookSelect, new object[] { oldActiveSheet });

            // Reset the selection in the active sheet (some bugs make this change sometimes too)
            XlCall.Excel(XlCall.xlcSelect, oldSelectionOnActiveSheet, oldActiveCellOnActiveSheet);
        }
    }

    public static class UtilityFunctions
    {
        static bool? _supportsDynamicArrays;
        [ExcelFunction(IsHidden = true)]
        public static bool dnaSupportsDynamicArrays()
        {
            if (!_supportsDynamicArrays.HasValue)
            {
                try
                {
                    var result = XlCall.Excel(614, new object[] { 1 }, new object[] { true });
                    _supportsDynamicArrays = true;
                }
                catch
                {
                    _supportsDynamicArrays = false;
                }
            }
            return _supportsDynamicArrays.Value;
        }
    }

}