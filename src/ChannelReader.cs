using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace YouTube4K {
 public sealed class ChannelVideo {
  public string Id {get;set;}
  public string Title {get;set;}
  public double? Duration {get;set;}
  public string Url {get{return "https://www.youtube.com/watch?v="+Id;}}
  public string DurationText {get{
   if(!Duration.HasValue)return "未知";
   TimeSpan t=TimeSpan.FromSeconds(Duration.Value);
   return t.TotalHours>=1 ? String.Format("{0}:{1:00}:{2:00}",(int)t.TotalHours,t.Minutes,t.Seconds) : String.Format("{0}:{1:00}",(int)t.TotalMinutes,t.Seconds);
  }}
 }
 public sealed class ReadSummary {
  public int Completed {get;set;}
  public int DuplicateSources {get;set;}
  public List<string> Errors {get;private set;}
  public ReadSummary(){Errors=new List<string>();}
 }
 public sealed class ChannelReader {
  readonly string root;
  readonly LoginOptions login;
  public ChannelReader(string directory,LoginOptions authentication=null){root=Path.GetFullPath(directory);login=authentication??new LoginOptions();}
  public static string NormalizeSourceUrl(string input,string tab){
   Uri uri;
   if(!Uri.TryCreate(input.Trim(),UriKind.Absolute,out uri) || (uri.Scheme!="https" && uri.Scheme!="http") ||
      !new[]{"youtube.com","www.youtube.com","m.youtube.com","youtu.be","www.youtu.be"}.Contains(uri.Host.ToLowerInvariant()) || uri.UserInfo.Length!=0 || !uri.IsDefaultPort)
    throw new ArgumentException("请填写有效的 YouTube 视频、播放列表或频道链接。");
   Match list=Regex.Match(uri.Query,@"(?:\?|&)list=([^&]*)");
   if(list.Success){string playlist=Uri.UnescapeDataString(list.Groups[1].Value);if(!Regex.IsMatch(playlist,@"^[A-Za-z0-9_-]{3,200}$"))throw new ArgumentException("播放列表编号无效。");return "https://www.youtube.com/playlist?list="+playlist;}
   string[] parts=Uri.UnescapeDataString(uri.AbsolutePath).Trim('/').Split('/');string id=null;
   if(uri.Host=="youtu.be" || uri.Host=="www.youtu.be"){if(parts.Length==1)id=parts[0];}
   else if(parts.Length==1 && parts[0]=="watch"){Match v=Regex.Match(uri.Query,@"(?:\?|&)v=([^&]+)");if(v.Success)id=Uri.UnescapeDataString(v.Groups[1].Value);}
   else if(parts.Length==2 && new[]{"shorts","live","embed"}.Contains(parts[0]))id=parts[1];
   if(id!=null && Regex.IsMatch(id,@"^[A-Za-z0-9_-]{11}$"))return "https://www.youtube.com/watch?v="+id;
   return NormalizeUrl(input,tab);
  }
  public ReadSummary ReadMany(string input,string tab,Action<ChannelVideo> onVideo,Action<string> onProgress,CancellationToken cancel,Action<string> onError=null){
   if(String.IsNullOrWhiteSpace(input))throw new ArgumentException("请粘贴至少一个链接，每行一个。");
   string[] lines=input.Replace("\r\n","\n").Replace('\r','\n').Split('\n');
   int total=lines.Count(s=>!String.IsNullOrWhiteSpace(s)),current=0;
   var sources=new HashSet<string>();var videos=new HashSet<string>();var result=new ReadSummary();
   for(int i=0;i<lines.Length;i++){
    cancel.ThrowIfCancellationRequested();string line=lines[i].Trim();if(line.Length==0)continue;current++;
    onProgress(String.Format("正在读取第 {0}/{1} 个地址，可随时停止…",current,total));
    try{
     string normalized=NormalizeSourceUrl(line,tab);if(!sources.Add(normalized)){result.DuplicateSources++;continue;}
     ReadUrl(normalized,v=>{if(videos.Add(v.Id))onVideo(v);},cancel);result.Completed++;
    }catch(OperationCanceledException){throw;}
    catch(Exception ex){string error=String.Format("第 {0} 行：{1}\r\n{2}",i+1,line,ex.Message);result.Errors.Add(error);if(onError!=null)onError(error);}
   }
   cancel.ThrowIfCancellationRequested();return result;
  }
  public static string NormalizeUrl(string input,string tab){
   if(!new[]{"videos","shorts","streams"}.Contains(tab))throw new ArgumentException("请选择视频类型。");
   Uri uri;
   if(!Uri.TryCreate(input.Trim(),UriKind.Absolute,out uri) || (uri.Scheme!="https" && uri.Scheme!="http") ||
      !new[]{"youtube.com","www.youtube.com","m.youtube.com"}.Contains(uri.Host.ToLowerInvariant()) ||
      uri.UserInfo.Length!=0 || !uri.IsDefaultPort)throw new ArgumentException("请粘贴 YouTube 频道链接，例如 https://www.youtube.com/@频道名称");
   string[] parts=Uri.UnescapeDataString(uri.AbsolutePath).Trim('/').Split('/');
   if(parts.Length==1 && (parts[0]=="watch" || parts[0]=="playlist")){
    Match list=Regex.Match(uri.Query,@"(?:\?|&)list=([^&]+)");
    string id=list.Success?Uri.UnescapeDataString(list.Groups[1].Value):"";
    if(Regex.IsMatch(id,@"^[A-Za-z0-9_-]{3,200}$"))return "https://www.youtube.com/playlist?list="+id;
    throw new ArgumentException("请粘贴频道或播放列表链接；单个视频请使用主窗口下载。");
   }
   int length=parts[0].StartsWith("@",StringComparison.Ordinal) && parts[0].Length>1 ? 1 :
       parts.Length>=2 && new[]{"channel","c","user"}.Contains(parts[0]) && parts[1].Length>0 ? 2 : 0;
   if(length==0 || parts.Length>length+1 || parts.Any(p=>p=="." || p==".." || p.Any(Char.IsControl)) ||
      (parts.Length==length+1 && !new[]{"videos","shorts","streams","featured"}.Contains(parts[length])))
    throw new ArgumentException("这里需要频道主页或播放列表链接。");
   string path=String.Join("/",parts.Take(length).Select(p=>Uri.EscapeDataString(p).Replace("%40","@")));
   return "https://www.youtube.com/"+path+"/"+tab;
  }
  public static ChannelVideo ParseVideo(string json){
   var data=new JavaScriptSerializer {MaxJsonLength=8388608}.Deserialize<Dictionary<string,object>>(json);
   if(data==null)throw new FormatException("频道视频信息不是有效对象。");
   string id=Text(data,"id"),availability=Text(data,"availability"),live=Text(data,"live_status");
   if(!Regex.IsMatch(id,@"^[A-Za-z0-9_-]{11}$") || new[]{"private","premium_only","subscriber_only","needs_auth"}.Contains(availability) ||
      new[]{"is_live","is_upcoming","post_live"}.Contains(live))return null;
   double value;double? duration=null;
   if(Double.TryParse(Text(data,"duration"),NumberStyles.Float,CultureInfo.InvariantCulture,out value) && value>=0 && value<86400000)duration=value;
   string title=Text(data,"title");
   if(title=="[Private video]" || title=="[Deleted video]")return null;
   return new ChannelVideo {Id=id,Title=String.IsNullOrWhiteSpace(title)?id:title,Duration=duration};
  }
  static string Text(Dictionary<string,object> data,string key){object value;return data.TryGetValue(key,out value) && value!=null ? Convert.ToString(value,CultureInfo.InvariantCulture) : "";}
  public void Read(string input,string tab,Action<ChannelVideo> onVideo,CancellationToken cancel){
   ReadUrl(NormalizeUrl(input,tab),onVideo,cancel);
  }
  void ReadUrl(string url,Action<ChannelVideo> onVideo,CancellationToken cancel){
   string exe=Path.Combine(root,"yt-dlp.exe");
   if(!File.Exists(exe))throw new FileNotFoundException("缺少 yt-dlp.exe，请将绿色版完整解压。",exe);
   cancel.ThrowIfCancellationRequested();
   var args=new List<string>{"--ignore-config","--flat-playlist","--dump-json","--skip-download","--lazy-playlist","--ignore-errors","--no-warnings","--ignore-no-formats-error","--encoding","utf-8","--socket-timeout","15","--retries","1","--extractor-retries","1","--extractor-args","youtube:raise_incomplete_data=true"};
   if(File.Exists(Path.Combine(root,"deno.exe")))args.AddRange(new[]{"--js-runtimes","deno:"+Path.Combine(root,"deno.exe")});
   using(var session=new LoginSession(login)){
   args.AddRange(session.Arguments);
   args.AddRange(new[]{"--",url});
   var info=new ProcessStartInfo(exe,String.Join(" ",args.Select(DownloadQueue.QuoteArgument))){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false)};
   var ids=new HashSet<string>();string error="";Exception parseError=null;
   using(var process=new Process {StartInfo=info}){
    process.OutputDataReceived+=(s,e)=>{
     if(String.IsNullOrWhiteSpace(e.Data) || cancel.IsCancellationRequested)return;
     try{var video=ParseVideo(e.Data);if(video!=null && ids.Add(video.Id))onVideo(video);}
     catch(Exception ex){parseError=ex;}
    };
    process.ErrorDataReceived+=(s,e)=>{if(!String.IsNullOrWhiteSpace(e.Data))error=EngineMessages.Failure(e.Data);};
    try{
     process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();
     while(!process.WaitForExit(100))cancel.ThrowIfCancellationRequested();
     process.WaitForExit();cancel.ThrowIfCancellationRequested();
     if(process.ExitCode!=0)throw new InvalidOperationException("频道读取未完成："+(error.Length>0?error:"请检查频道链接和网络连接。"));
     if(parseError!=null)throw new InvalidOperationException("部分视频信息无法解析，请检查下载引擎版本。",parseError);
    }finally{
     try{if(!process.HasExited){ProcessSuspension.KillTree(process.Id);process.WaitForExit(3000);}}catch(InvalidOperationException){}
    }
   }
   }
  }
 }
}
