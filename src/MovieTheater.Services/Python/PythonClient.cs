using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace MovieTheater.Services.Python
{
    public class PythonClient
    {
        private readonly PythonOptions pythonOptions;

        public PythonClient(IOptions<PythonOptions> pythonOptions)
        {
            this.pythonOptions = pythonOptions.Value;
        }

        public int Exec(params string[] cliParamters)
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = pythonOptions.PyPath;
            start.WorkingDirectory = System.Environment.CurrentDirectory;

            foreach (var param in cliParamters)
            {
                start.ArgumentList.Add(param);
            }

            start.UseShellExecute = true;// Do not use OS shell
            start.CreateNoWindow = true; // We don't need new window

            var process = Process.Start(start);
            process.WaitForExit();

            return process.ExitCode;
        }
    }
}
