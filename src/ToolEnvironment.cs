using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
namespace YouTube4K {
 public static class ToolEnvironment {
  public static void Validate(string root,CancellationToken cancel=default(CancellationToken)){
   foreach(string tool in new[]{"yt-dlp.exe","deno.exe","ffmpeg.exe","ffprobe.exe"}){
    cancel.ThrowIfCancellationRequested();
    string exe=Path.Combine(root,tool);if(!File.Exists(exe))throw new InvalidOperationException("缺少 "+tool+"，请完整解压绿色版。");
    var info=new ProcessStartInfo(exe,tool=="yt-dlp.exe"?"--ignore-config --version":tool=="deno.exe"?"--version":"-version") {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false)};
    using(var process=new Process {StartInfo=info}){
     bool output=false;process.OutputDataReceived+=(s,e)=>{if(!String.IsNullOrWhiteSpace(e.Data))output=true;};process.ErrorDataReceived+=(s,e)=>{if(!String.IsNullOrWhiteSpace(e.Data))output=true;};
     try{
      process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();
      var end=DateTime.UtcNow.AddSeconds(5);
      while(!process.WaitForExit(100)){cancel.ThrowIfCancellationRequested();if(DateTime.UtcNow>=end)throw new InvalidOperationException(tool+" 启动检查超时，请检查本地文件。");}
      process.WaitForExit();if(process.ExitCode!=0 || !output)throw new InvalidOperationException(tool+" 无法正常运行，请检查程序版本和本机运行环境。");
     }catch(System.ComponentModel.Win32Exception){throw new InvalidOperationException(tool+" 无法启动，请检查文件权限和 Windows 兼容性。");}
     finally{try{if(!process.HasExited){ProcessSuspension.KillTree(process.Id);process.WaitForExit(3000);}}catch(InvalidOperationException){}}
    }
   }
  }
 }
}
