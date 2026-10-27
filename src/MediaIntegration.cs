using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace YouTube4K {
 [Flags] public enum MediaOutputs { Video=1, Audio=2, Transcript=4 }
 public static class VideoQuality {
  public static readonly int[] Heights={4320,3456,2160,1440,1080,720};
  public static readonly string[] Labels={"最高 8K（4320p）","最高 6K（3456p）","最高 4K（2160p）","最高 1440p","最高 1080p","最高 720p"};
  public static string Label(int height){int index=Array.IndexOf(Heights,height);return index<0?"未知清晰度":Labels[index];}
  public static string Format(int height){string limits="[height<="+height+"]"+(height==4320?"[width<=7680]":height==3456?"[width<=6144]":"");return "bestvideo"+limits+"+bestaudio/best"+limits;}
 }
 public sealed partial class DownloadQueue {
  public MediaOutputs OutputSelection {get;private set;}
  public string TranscriptLanguage {get;private set;}
  public string TranscriptSource {get;private set;}
  public void SetOutputs(MediaOutputs value,string language="zh",string source="auto"){
   ThrowIfDisposed();
   if((int)value<1 || (int)value>7)throw new ArgumentException("请至少选择视频、音频或文字稿中的一项。");
   if(!new[]{"auto","zh","en","ja","ko"}.Contains(language) || !new[]{"auto","whisper"}.Contains(source))throw new ArgumentException("文字稿设置无效。");
   if((value&MediaOutputs.Transcript)==0){language="auto";source="auto";}
   OutputSelection=value;TranscriptLanguage=language;TranscriptSource=source;
  }
  string MediaPython {get{
   string portable=Path.Combine(BaseDirectory,"engine","runtime","python.exe");
   return File.Exists(portable)?portable:Path.Combine(BaseDirectory,"engine",".venv","Scripts","python.exe");
  }}
  void ValidateMedia(CancellationToken cancel){
   if(!File.Exists(MediaPython) || !File.Exists(Path.Combine(BaseDirectory,"engine","media_worker.py")))throw new InvalidOperationException("缺少音频 / 文字稿运行组件，请完整解压合并版文件夹。");
   using(var process=new Process()){
    process.StartInfo=new ProcessStartInfo(MediaPython,"--version"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    try{
     process.Start();var end=DateTime.UtcNow.AddSeconds(8);
     while(!process.WaitForExit(100)){cancel.ThrowIfCancellationRequested();if(DateTime.UtcNow>end)throw new InvalidOperationException("音频 / 文字稿运行环境检查超时。");}
     if(process.ExitCode!=0)throw new InvalidOperationException("音频 / 文字稿运行环境无法启动，请完整解压合并版。");
    }finally{try{if(!process.HasExited)ProcessSuspension.KillTree(process.Id);}catch(InvalidOperationException){}}
   }
  }
  void BeginMedia(DownloadTask task){
   task.AudioPath=task.TranscriptPath=task.TextPath=task.FilePath=null;
   task.Progress="准备获取所选内容";
   try{task.Login=new LoginSession(Authentication);}catch(ArgumentException){throw new AuthenticationPreparationException();}catch(InvalidOperationException){throw new AuthenticationPreparationException();}
   var outputs=new List<string>();
   if((task.Outputs&MediaOutputs.Video)!=0)outputs.Add("video");
   if((task.Outputs&MediaOutputs.Audio)!=0)outputs.Add("audio");
   if((task.Outputs&MediaOutputs.Transcript)!=0)outputs.Add("transcript");
   var configuration=new Dictionary<string,object>{
    {"tools_dir",BaseDirectory},{"destination",task.DestinationDirectory},{"url",task.Url},{"video_id",task.VideoId},
    {"outputs",outputs},{"height",task.TargetHeight},{"language",task.Language},{"source",task.TranscriptMode},
    {"auth_args",task.Login.Arguments},{"job_key",task.MediaJobKey},{"result_path",task.MediaResultPath},
    {"suffix",task.RedownloadRound>0?"_重下"+task.RedownloadRound+"_"+task.RedownloadKey:""}
   };
   BeginProcess(task,MediaPython,new[]{"-u",Path.Combine(BaseDirectory,"engine","media_worker.py")},new JavaScriptSerializer().Serialize(configuration));
  }
  static string MediaFailure(string code){
   switch(code){
    case "authentication":return "YouTube 要求登录验证，请检查验证方式并测试连接，再手动开始。";
    case "cookies_locked":return "无法读取浏览器 Cookie，可能被浏览器占用。请手动关闭所选浏览器后测试连接。";
    case "decrypt":return "无法解密浏览器 Cookie，请检查浏览器支持情况或自行选择本地 Cookie 文件。";
    case "network":return "网络连接失败或长时间没有响应，请检查网络后手动重试。";
    case "unavailable":return "视频不可用、已删除或当前账号没有访问权限。";
    case "missing_model":return "缺少本地转写模型，请使用包含 engine/models 的完整合并版。";
    case "transcription":return "本地转写失败，请检查 NVIDIA 显卡、驱动和转写组件；已下载的文件已保留。";
    case "no_speech":return "未识别到可用语音，未生成文字稿；已下载的文件已保留。";
    case "files":return "文件保存失败，请检查磁盘空间、写入权限或同名文件占用。";
    case "verify":return "文件已获取，但音视频完整性检查未通过。";
    case "media":return "音视频处理失败，可能没有可提取的音轨；已完成的文件已保留。";
    case "configuration":return "音频 / 文字稿配置无效，请重新添加任务或检查合并版组件。";
    default:return "内容获取失败，请检查网络或视频可用性后手动重试。";
   }
  }
  void ReceiveMedia(DownloadTask task,string line){
   if(!line.StartsWith("@@MEDIA ",StringComparison.Ordinal))return;
   try{
    var value=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(line.Substring(8));
    string kind=GetString(value,"kind");
    if(kind=="title"){task.Title=GetString(value,"text");return;}
    if(kind=="error"){
     string code=GetString(value,"code");
     bool auth=code=="authentication" || code=="cookies_locked" || code=="decrypt";
     if(auth){task.AuthenticationFailed=true;verificationRequired=true;Interlocked.Increment(ref verificationVersion);}
     if(auth || !task.AuthenticationFailed){task.ErrorOutput=MediaFailure(code);task.Progress=task.ErrorOutput;}
     // Only our fixed message is recorded. Never log the incoming JSON or tool text.
     AppendMediaLog(task,MediaFailure(code));return;
    }
    if(kind!="progress" || task.AuthenticationFailed)return;
    string phase=GetString(value,"phase"),message;
    switch(phase){
     case "metadata":message="读取视频与原语言字幕信息";break;
     case "download":message="下载 / 合并视频";break;
     case "audio":message="下载音频";break;
     case "extract_audio":message="提取 MP3 音频";break;
     case "subtitles":message="获取原语言字幕";break;
     case "subtitle_fallback":message="字幕暂不可用，准备本地识别";break;
     case "waiting_gpu":message="等待前一个本地转写完成";break;
     case "waiting_file":message="等待同一视频的文件处理完成";break;
     case "model":message="加载本地转写模型";break;
     case "transcribe":message="本地识别原话文字稿";break;
     case "verify":message="检查音视频文件";break;
     default:return;
    }
    if(value.ContainsKey("percent")){double percent; if(Double.TryParse(Convert.ToString(value["percent"],System.Globalization.CultureInfo.InvariantCulture),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out percent) && !Double.IsNaN(percent) && !Double.IsInfinity(percent))message+=" · "+Math.Max(0,Math.Min(100,percent)).ToString("F1")+"%";}
    task.Progress=message;
   }catch(ArgumentException){}catch(InvalidOperationException){}
  }
  static void AppendMediaLog(DownloadTask task,string message){try{File.AppendAllText(task.LogPath,message+Environment.NewLine,new UTF8Encoding(false));}catch(IOException){}catch(UnauthorizedAccessException){}}
  static string GetString(Dictionary<string,object> data,string key){object value;return data.TryGetValue(key,out value)?value as string??"":"";}
  string ResultFile(Dictionary<string,object> files,string key,DownloadTask task){
   string value=GetString(files,key);if(String.IsNullOrWhiteSpace(value))return null;
   string path=Path.GetFullPath(value),directory=Path.GetFullPath(task.DestinationDirectory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(!path.StartsWith(directory,StringComparison.OrdinalIgnoreCase) || !File.Exists(path))return null;
   return path;
  }
  void FinishMedia(DownloadTask task,int exit){
   try{
    var result=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(task.MediaResultPath,Encoding.UTF8));
    if(GetString(result,"job_key")!=task.MediaJobKey)throw new IOException();
    var files=result["files"] as Dictionary<string,object>;if(files==null)throw new IOException();
    task.AudioPath=ResultFile(files,"audio",task);task.TranscriptPath=ResultFile(files,"md",task);task.TextPath=ResultFile(files,"txt",task);
    string video=ResultFile(files,"video",task);task.FilePath=video??task.AudioPath??task.TranscriptPath;
    task.Resolution=video!=null?GetString(result,"resolution"):task.AudioPath!=null?"音频":"文字稿";
    bool all=((task.Outputs&MediaOutputs.Video)==0 || video!=null) && ((task.Outputs&MediaOutputs.Audio)==0 || task.AudioPath!=null) && ((task.Outputs&MediaOutputs.Transcript)==0 || task.TranscriptPath!=null && task.TextPath!=null);
    if(exit==0 && GetString(result,"status")=="completed" && all){task.State=TaskState.Completed;task.Progress="完成 · "+task.ContentText+(task.TranscriptPath!=null?" · "+GetString(result,"source"):"");AppendMediaLog(task,"所选内容已完成。");}
    else {task.State=TaskState.Failed;task.Progress=String.IsNullOrEmpty(task.ErrorOutput)?MediaFailure(GetString(result,"error")):task.ErrorOutput;if(task.FilePath!=null)task.Progress+=" 已有成果已保留。";}
   }catch(Exception){task.State=TaskState.Failed;task.Progress=String.IsNullOrEmpty(task.ErrorOutput)?"处理进程未正常完成，已有文件保留；请检查本地组件并手动重试。":task.ErrorOutput;}
  }
 }
}
