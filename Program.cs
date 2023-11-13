ProgramArgs parsedArgs = new();

Console.WriteLine("Attach the debugger and set breakpoints. Then press any key to continue.");
Console.ReadKey();

sourcelinkbug.ArgumentParser.ParseArguments(args, parsedArgs);

Console.WriteLine("Hello, World!");
if (parsedArgs.Echo != null)
{
    Console.WriteLine(parsedArgs.Echo);
}
Console.WriteLine("Press any key to exit.");
Console.ReadKey();

internal class ProgramArgs
{
    public string? Echo { get; set; }
}
