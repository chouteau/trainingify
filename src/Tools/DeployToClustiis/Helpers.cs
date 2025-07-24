using System.Diagnostics;

namespace DeployToClustiis;

public static class Helpers 
{
    public static void Process(string command, string arguments, string? workingDirectory = null)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(command);
        psi.Arguments = arguments;
        psi.CreateNoWindow = false;
        psi.UseShellExecute = false;
        psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
        psi.RedirectStandardError = true;
        psi.ErrorDialog = false;
        if (workingDirectory != null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        var mre = new System.Threading.ManualResetEvent(false);

        var process = new Process();
        process.StartInfo = psi;
        process.EnableRaisingEvents = true;
        process.ErrorDataReceived += (s, arg) =>
        {
            if (string.IsNullOrWhiteSpace(arg.Data))
            {
                return;
            }
            Console.WriteLine(arg.Data);
        };
        process.Exited += (s, arg) =>
        {
            mre.Set();
        };

        var start = process.Start();
        if (!start)
        {
            throw new Exception();
        }
        process.BeginErrorReadLine();

        mre.WaitOne();
        mre.Reset();
    }

    public static string GetParameterValue(this string[] args, string parameterName)
    {
        if (args == null
            || !args.Any()) 
        {
            return string.Empty;
        }

        string value = string.Empty;
        var nextisvalue = false;
        foreach (var item in args)
        {
            if (nextisvalue)
            {
                value = item;
                break;
            }
            if (item.Equals($"{parameterName}", StringComparison.InvariantCultureIgnoreCase))
            {
                nextisvalue = true;
            }
        }
        return $"{value}".Trim();
    }
}
