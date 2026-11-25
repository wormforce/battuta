namespace Battuta.Linux;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--gpui-runtime"])
        {
            await GpuiRuntimeHost.RunAsync();
            return 0;
        }

        Console.Error.WriteLine("Start Battuta with battuta-gpui.");
        return 2;
    }
}
