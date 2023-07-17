using System;
using AsyncHelloWorld;
namespace ConsoleAppTestDemo
{
  internal class Program
  {
    static void Main(string[] args)
    {
      Action<string> Display = Console.WriteLine;
      HelloWorld.SayHelloAsync("johnny");
      Display("Press any key to exit:");
      Console.ReadKey();
    }
  }
}
