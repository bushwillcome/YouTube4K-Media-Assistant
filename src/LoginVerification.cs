using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Security.AccessControl;
using System.Security.Principal;

namespace YouTube4K {
 public enum LoginMode { None, Chrome, Edge, CookieFile }
 public sealed class LoginOptions {
  public LoginMode Mode {get;private set;}
  public string CookiePath {get;private set;}
  public LoginOptions(LoginMode mode=LoginMode.None,string cookiePath=null){
   if(!Enum.IsDefined(typeof(LoginMode),mode))throw new ArgumentException("登录验证方式无效。");
   Mode=mode;CookiePath=cookiePath;
  }
  public string[] Arguments(){
   if(Mode==LoginMode.Chrome)return new[]{"--no-cookies","--cookies-from-browser","chrome"};
   if(Mode==LoginMode.Edge)return new[]{"--no-cookies","--cookies-from-browser","edge"};
   if(Mode==LoginMode.CookieFile){
    if(String.IsNullOrWhiteSpace(CookiePath) || !Path.IsPathRooted(CookiePath) || !File.Exists(CookiePath))
     throw new ArgumentException("Cookie 文件不存在或无法访问，请重新选择本地文件。");
    return new[]{"--no-cookies-from-browser","--cookies",CookiePath};
   }
   return new[]{"--no-cookies","--no-cookies-from-browser"};
  }
 }
 public sealed class LoginSession : IDisposable {
  public string[] Arguments {get;private set;}
  string directory;
  public LoginSession(LoginOptions login){
   login=login??new LoginOptions();Arguments=login.Arguments();
   if(login.Mode!=LoginMode.CookieFile)return;
   directory=Path.Combine(Path.GetTempPath(),"YouTube4K-auth-"+Guid.NewGuid().ToString("N"));
   try{
    var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
    using(var identity=WindowsIdentity.GetCurrent()){
     security.SetOwner(identity.User);security.AddAccessRule(new FileSystemAccessRule(identity.User,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
    }
    Directory.CreateDirectory(directory,security);
    string jar=Path.Combine(directory,"cookies.txt");File.Copy(login.CookiePath,jar,false);File.SetAttributes(jar,FileAttributes.Normal);
    var fileSecurity=new FileSecurity();fileSecurity.SetAccessRuleProtection(true,false);
    using(var identity=WindowsIdentity.GetCurrent()){
     fileSecurity.SetOwner(identity.User);fileSecurity.AddAccessRule(new FileSystemAccessRule(identity.User,FileSystemRights.FullControl,AccessControlType.Allow));
    }
    File.SetAccessControl(jar,fileSecurity);
    Arguments=new[]{"--no-cookies-from-browser","--cookies",jar};
   }catch(Exception){Dispose();throw new InvalidOperationException("无法读取所选 Cookie 文件或创建受保护的临时副本，请重新选择文件并检查本地权限。");}
  }
  public void Dispose(){
   if(directory==null)return;
   string jar=Path.Combine(directory,"cookies.txt");
   try{if(File.Exists(jar)){File.SetAttributes(jar,FileAttributes.Normal);File.Delete(jar);}if(Directory.Exists(directory))Directory.Delete(directory);directory=null;}
   catch(IOException){throw new InvalidOperationException("无法清理本次身份验证临时文件，请检查临时目录权限。");}
   catch(UnauthorizedAccessException){throw new InvalidOperationException("无法清理本次身份验证临时文件，请检查临时目录权限。");}
  }
 }
 public static class EngineMessages {
  public static bool IsAuthenticationFailure(string line){
   string text=(line??"").ToLowerInvariant();
   return text.Contains("confirm") && text.Contains("bot") || text.Contains("sign in") || text.Contains("log in") ||
    text.Contains("cookie") || text.Contains("dpapi") || text.Contains("decrypt") || text.Contains("app-bound") || text.Contains("keyring");
  }
  public static string Failure(string diagnostic){
   string text=(diagnostic??"").ToLowerInvariant();
   if(text.Contains("decrypt") || text.Contains("dpapi") || text.Contains("app-bound"))
    return "无法解密浏览器 Cookie。当前浏览器或 Windows 加密方式可能不受支持；请自行选择 Netscape 格式的 Cookie 文件，再测试连接。";
   if(text.Contains("cookie") && (text.Contains("copy") || text.Contains("locked") || text.Contains("permission") || text.Contains("database")))
    return "无法读取浏览器 Cookie，可能被浏览器占用或没有权限。请关闭浏览器后测试；仍失败时可自行选择 Cookie 文件。";
   if(text.Contains("cookie") && (text.Contains("netscape") || text.Contains("invalid") || text.Contains("format") || text.Contains("load")))
    return "Cookie 文件无法读取或格式无效。请选择可读取的 Netscape 格式 Cookie 文件。";
   if(text.Contains("confirm") && text.Contains("bot") || text.Contains("sign in") || text.Contains("log in"))
    return "YouTube 仍要求登录验证。请在所选浏览器中打开该视频并完成验证，再测试连接；当前 Cookie 也可能已失效。";
   if(text.Contains("cookie") || text.Contains("keyring"))
    return "浏览器 Cookie 无法读取或登录验证失败。请检查所选浏览器的登录状态，或自行选择 Cookie 文件后测试连接。";
   if(text.Contains("timed out") || text.Contains("timeout"))return "连接超时，请检查网络后手动测试连接。";
   if(text.Contains("403"))return "HTTP 403：服务器拒绝访问，请检查视频是否可访问，并测试连接。";
   if(text.Contains("429"))return "HTTP 429：请求过于频繁，请稍后手动重试。";
   if(text.Contains("private") || text.Contains("unavailable") || text.Contains("not available"))return "视频不可访问、已删除或受到访问限制。";
   if(text.Contains("incomplete data"))return "列表信息不完整，请检查网络后重新读取。";
   if(text.Contains("resolve") || text.Contains("network") || text.Contains("connection"))return "无法连接下载服务，请检查网络后测试连接。";
   return "下载引擎报告错误，请检查链接、网络及登录验证方式后测试连接。";
  }
  public static string SafeLogLine(string line){
   if(String.IsNullOrWhiteSpace(line))return null;
   string lower=line.ToLowerInvariant();
   // Keep only known diagnostics. Never persist arbitrary engine output or headers.
   if(lower.Contains("error:") || lower.Contains("warning:"))return Failure(line);
   if(lower.Contains("cookie") || lower.Contains("authorization") || lower.Contains("bearer") || lower.Contains("token") || lower.Contains("password") || lower.Contains("secret"))return "身份验证诊断详情已省略。";
   if(line.StartsWith("TITLE:",StringComparison.Ordinal))return "已获取视频标题。";
   if(line.StartsWith("[download]",StringComparison.Ordinal)){
    var progress=Regex.Match(line,@"^\[download\]\s+\d+(?:\.\d+)?%(?:\s+of\s+~?[\d.]+\s*[KMGTPE]?i?B)?(?:\s+at\s+[\d.]+\s*[KMGTPE]?i?B/s)?(?:\s+ETA\s+[\d:]+)?");
    return progress.Success?progress.Value:"[download] 正在下载视频。";
   }
   if(line.StartsWith("[Merger]",StringComparison.Ordinal))return "[Merger] 正在合并音视频。";
   if(line.StartsWith("[VideoRemuxer]",StringComparison.Ordinal))return "[VideoRemuxer] 正在封装视频。";
   if(line=="暂停后视频地址失效，自动刷新并续传一次。")return line;
   return null;
  }
 }
 public sealed class ConnectionResult {
  public bool Success {get;internal set;}
  public string Message {get;internal set;}
 }
 public sealed class ConnectionTester {
  readonly string root;
  public ConnectionTester(string directory){root=Path.GetFullPath(directory);}
  public ConnectionResult Test(string videoUrl,LoginOptions login,CancellationToken cancel,int timeoutSeconds=60){
   string url;
   using(var parser=new DownloadQueue(root,1)){
    parser.AddLinks(videoUrl);if(parser.Items.Count!=1)throw new ArgumentException("测试连接请使用一个 YouTube 视频地址。");url=parser.Items[0].Url;
   }
   var args=new List<string>{"--ignore-config","--simulate","--no-playlist","--no-warnings","--encoding","utf-8","--socket-timeout","15","--retries","0","--extractor-retries","0","--print","TITLE:%(title)s","-f","bestvideo[height<=2160]+bestaudio/best[height<=2160]"};
   using(var session=new LoginSession(login)){
   args.AddRange(session.Arguments);
   if(File.Exists(Path.Combine(root,"deno.exe")))args.AddRange(new[]{"--js-runtimes","deno:"+Path.Combine(root,"deno.exe")});
   args.AddRange(new[]{"--",url});
   string exe=Path.Combine(root,"yt-dlp.exe");if(!File.Exists(exe))throw new FileNotFoundException("缺少 yt-dlp.exe，请完整解压软件。");
   bool metadata=false;string error=null;var gate=new object();
   var info=new ProcessStartInfo(exe,String.Join(" ",args.Select(DownloadQueue.QuoteArgument))){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false)};
   cancel.ThrowIfCancellationRequested();
   using(var process=new Process {StartInfo=info}){
    process.OutputDataReceived+=(s,e)=>{if(e.Data!=null && e.Data.StartsWith("TITLE:") && e.Data.Length>6)lock(gate){metadata=true;}};
    process.ErrorDataReceived+=(s,e)=>{if(!String.IsNullOrWhiteSpace(e.Data))lock(gate){error=EngineMessages.Failure(e.Data);}};
    try{
     process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();var end=DateTime.UtcNow.AddSeconds(timeoutSeconds);
     while(!process.WaitForExit(100)){
      cancel.ThrowIfCancellationRequested();if(DateTime.UtcNow>=end)return new ConnectionResult {Message="测试连接超时，已停止本次测试。请检查网络后手动重试。"};
     }
     process.WaitForExit();cancel.ThrowIfCancellationRequested();
     if(process.ExitCode==0 && metadata)return new ConnectionResult {Success=true,Message="连接成功：已获取视频信息。本次没有下载视频。"};
     return new ConnectionResult {Message=error??"没有获取到有效的视频信息，请检查链接、网络和登录验证方式。"};
    }finally{
     try{if(!process.HasExited){ProcessSuspension.KillTree(process.Id);process.WaitForExit(3000);}}catch(InvalidOperationException){}
    }
   }
   }
  }
 }
}
