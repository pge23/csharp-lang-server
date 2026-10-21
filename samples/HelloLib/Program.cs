namespace HelloLib;

public static class Program
{
    public static void Run()
    {
        IClock clock = new SystemClock();
        var greeter = new Greeter(clock);
        System.Console.WriteLine(greeter.Greet("world"));
        System.Console.WriteLine(greeter.Greet("Roslyn"));
    }
}
