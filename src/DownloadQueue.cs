using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace YouTube4K {
 internal sealed class AuthenticationPreparationException : InvalidOperationException {
  public AuthenticationPreparationException():base("登录验证准备失败，请检查 Cookie 文件及读取权限，处理后再手动开始。"){ }
 }
 public enum TaskState { Queued, Running, Paused, Completed, Failed }
 public sealed class DownloadTask {
  public int Number {get; internal set;}
  public string VideoId {get; internal set;}
  public string Url {get; internal set;}
  public string Title {get; internal set;}
  public TaskState State {get; internal set;}
  public string Progress {get; internal set;}
  public string Resolution {get; internal set;}
  public string FilePath {get; internal set;}
  public string LogPath {get; internal set;}
  public string DestinationDirectory {get; internal set;}
  public int TargetHeight {get;internal set;}
  public MediaOutputs Outputs {get;internal set;}
  public string Language {get;internal set;}
  public string TranscriptMode {get;internal set;}
  public string AudioPath {get;internal set;}
  public string TranscriptPath {get;internal set;}
  public string TextPath {get;internal set;}
  public string ContentText {get{return String.Join(" + ",new[]{(Outputs&MediaOutputs.Video)!=0?"视频":null,(Outputs&MediaOutputs.Audio)!=0?"音频":null,(Outputs&MediaOutputs.Transcript)!=0?"文字稿":null}.Where(s=>s!=null));}}
  internal bool Enhanced {get{return Outputs!=MediaOutputs.Video;}}
  internal string MediaResultPath {get{return PathList+".media.json";}}
  internal string MediaJobKey {get{return Path.GetFileName(PathList)+"-"+RedownloadRound+"-"+RedownloadKey;}}
  public string QualityText {get{return (Outputs&MediaOutputs.Video)!=0?VideoQuality.Label(TargetHeight):"—";}}
  internal bool Ready;
  internal int RedownloadRound;
  internal string RedownloadKey;
  internal string OutputTemplate {get{return (RedownloadRound>0?"%(title).120B":"%(title).180B")+" [%(id)s]"+(RedownloadRound>0?"_重下"+RedownloadRound+"_"+RedownloadKey:"")+".%(ext)s";}}
  public int ProcessId {get {return Worker==null ? 0 : Worker.Id;}}
  internal Process Worker;
  internal LoginSession Login;
  internal bool Probing, WasResumed, AuthenticationFailed;
  internal int Retries;
  internal string PathList, ProbeOutput="", ErrorOutput="";
  internal readonly object Gate=new object();
  internal readonly ProcessSuspension Suspension=new ProcessSuspension();
 }

 public sealed partial class DownloadQueue : IDisposable {
  public readonly string BaseDirectory;
  public string DownloadsDirectory {get; private set;}
  public int Concurrency {get;private set;}
  public int TargetHeight {get;private set;}
  volatile bool verificationRequired;
  public bool VerificationRequired {get{return verificationRequired;}}
  int verificationVersion;
  public int VerificationVersion {get{return Interlocked.CompareExchange(ref verificationVersion,0,0);}}
  public int WaitingCount {get{return Items.Count(t=>t.Worker==null && (t.State==TaskState.Queued || t.State==TaskState.Paused));}}
  public readonly List<DownloadTask> Items=new List<DownloadTask>();
  public bool Paused {get; private set;}
  public bool Started {get; private set;}
  public bool IsDisposed {get{return disposed;}}
  public LoginOptions Authentication {get;set;}
  public string CleanupWarning {get;private set;}
  public bool HasUnfinished {get {return Items.Any(t=>t.State==TaskState.Queued || t.State==TaskState.Running || t.State==TaskState.Paused);}}
  readonly string session=DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8);
  bool disposed;int nextNumber;
  public DownloadQueue(string directory,int concurrency=1) {
   if(concurrency<1 || concurrency>3) throw new ArgumentException("同时下载数量只能为 1～3。");
   BaseDirectory=Path.GetFullPath(directory);Concurrency=concurrency;TargetHeight=2160;Authentication=new LoginOptions();
   SetOutputs(MediaOutputs.Video);
   DownloadsDirectory=Path.Combine(BaseDirectory,"downloads");
   try {
    string settings=Path.Combine(BaseDirectory,"YouTube4K.settings.txt");
    if(File.Exists(settings)) {
     string saved=File.ReadAllText(settings,Encoding.UTF8).Trim();
     if(Path.IsPathRooted(saved))DownloadsDirectory=Path.GetFullPath(saved);
    }
   }catch(ArgumentException){}catch(IOException){}catch(UnauthorizedAccessException){}catch(NotSupportedException){}
  }
  public void SetConcurrency(int value){ThrowIfDisposed();if(value<1 || value>3)throw new ArgumentException("同时下载数量只能为 1～3。");Concurrency=value;}
  public void SetTargetHeight(int value){ThrowIfDisposed();if(!VideoQuality.Heights.Contains(value))throw new ArgumentException("请选择支持的清晰度。");TargetHeight=value;}
  public void SetDownloadsDirectory(string directory) {
   ThrowIfDisposed();
   if(String.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))throw new ArgumentException("请选择完整的文件夹路径。");
   string selected=Path.GetFullPath(directory);
   Directory.CreateDirectory(selected);
   string check=Path.Combine(selected,".yt-write-"+Guid.NewGuid().ToString("N")+".tmp");
   using(FileStream stream=new FileStream(check,FileMode.CreateNew,FileAccess.Write,FileShare.None)){}File.Delete(check);
   File.WriteAllText(Path.Combine(BaseDirectory,"YouTube4K.settings.txt"),selected,new UTF8Encoding(false));
   DownloadsDirectory=selected;
  }
  public int AddLinks(string input) {
   ThrowIfDisposed();
   List<KeyValuePair<string,string>> links=new List<KeyValuePair<string,string>>();
   foreach(string line in input.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)) {
    string text=line.Trim();if(text.Length==0)continue;
    Uri uri;
    if(!Uri.TryCreate(text,UriKind.Absolute,out uri) || (uri.Scheme!="https" && uri.Scheme!="http"))throw new ArgumentException("链接格式不正确："+text);
    string host=uri.Host.ToLowerInvariant(),id=null;
    if(host=="youtu.be" || host=="www.youtu.be") id=uri.AbsolutePath.Trim('/').Split('/')[0];
    else if(host=="youtube.com" || host.EndsWith(".youtube.com",StringComparison.Ordinal)) {
     Match query=Regex.Match(uri.Query,@"(?:\?|&)v=([^&]+)");
     if(query.Success)id=Uri.UnescapeDataString(query.Groups[1].Value);
     else {string[] parts=uri.AbsolutePath.Trim('/').Split('/');if(parts.Length>=2 && (parts[0]=="shorts" || parts[0]=="live" || parts[0]=="embed"))id=parts[1];}
    }
    if(id==null || !Regex.IsMatch(id,@"^[A-Za-z0-9_-]{11}$"))throw new ArgumentException("请使用 YouTube 单个视频链接（每行一个）："+text);
    links.Add(new KeyValuePair<string,string>(id,"https://www.youtube.com/watch?v="+id));
   }
   int count=0;
   foreach(var link in links) {
    if(Items.Any(t=>t.VideoId==link.Key && t.Outputs==OutputSelection && t.Language==TranscriptLanguage && t.TranscriptMode==TranscriptSource))continue;
    int number=++nextNumber;
    Items.Add(new DownloadTask {Number=number,VideoId=link.Key,Url=link.Value,Title=link.Key,State=TaskState.Queued,Progress="已收集，等待手动开始",Resolution="",TargetHeight=TargetHeight,Outputs=OutputSelection,Language=TranscriptLanguage,TranscriptMode=TranscriptSource,DestinationDirectory=DownloadsDirectory,LogPath=Path.Combine(DownloadsDirectory,"logs",session+"-"+number+".log"),PathList=Path.Combine(DownloadsDirectory,"logs",session+"-"+number+".path")});count++;
   }
   return count;
  }
  public void ValidateStart(CancellationToken cancel=default(CancellationToken)) {
   ThrowIfDisposed();
   var pending=Items.Where(t=>t.Worker==null && (t.State==TaskState.Queued || t.State==TaskState.Paused)).ToArray();
   if(pending.Length==0)throw new InvalidOperationException("没有待下载任务，请先添加链接。");
   foreach(var task in pending){
    if(!Regex.IsMatch(task.VideoId??"",@"^[A-Za-z0-9_-]{11}$") || task.Url!="https://www.youtube.com/watch?v="+task.VideoId)throw new InvalidOperationException("任务链接无效，请删除后重新添加。");
    if(!VideoQuality.Heights.Contains(task.TargetHeight))throw new InvalidOperationException("任务清晰度无效，请重新添加。");
   }
   (Authentication??new LoginOptions()).Arguments();
   foreach(string directory in pending.SelectMany(t=>new[]{t.DestinationDirectory,Path.Combine(t.DestinationDirectory,"logs")}).Distinct()){
    cancel.ThrowIfCancellationRequested();
    try{
     if(String.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))throw new IOException();
     Directory.CreateDirectory(directory);Directory.CreateDirectory(Path.Combine(directory,"logs"));
     string check=Path.Combine(directory,".yt-write-"+Guid.NewGuid().ToString("N")+".tmp");
     using(var stream=new FileStream(check,FileMode.CreateNew,FileAccess.Write,FileShare.None)){}File.Delete(check);
    }catch(Exception){throw new InvalidOperationException("任务保存目录无法写入，请检查列表中的保存位置及磁盘权限。");}
   }
   ToolEnvironment.Validate(BaseDirectory,cancel);
   if(pending.Any(t=>t.Enhanced))ValidateMedia(cancel);
  }
  public void Start(){ValidateStart();StartValidated();}
  internal void StartValidated() {
   ThrowIfDisposed();
   if(WaitingCount==0)throw new InvalidOperationException("没有待下载任务。");
   verificationRequired=false;
   foreach(var task in Items.Where(t=>t.Worker==null && (t.State==TaskState.Queued || t.State==TaskState.Paused))){task.Ready=true;task.State=TaskState.Queued;}
   Started=true;FillSlots();
  }
  public void Tick() {
   ThrowIfDisposed();
   if(VerificationRequired)foreach(var waiting in Items.Where(t=>t.Worker==null && t.Ready && t.State==TaskState.Queued))waiting.State=TaskState.Paused;
   foreach(var task in Items.Where(t=>t.Worker!=null).ToArray()) {
    if(!task.Worker.HasExited)continue;
    task.Worker.WaitForExit();int result=task.Worker.ExitCode;
    task.Suspension.Resume();task.Worker.Dispose();task.Worker=null;
    try{ReleaseLogin(task);}catch(Exception ex){task.State=TaskState.Failed;task.Progress=ex.Message;continue;}
    if(task.Enhanced){FinishMedia(task,result);continue;}
    if(task.Probing) {FinishProbe(task,result);continue;}
    if(result!=0) {
     if(!task.AuthenticationFailed && task.WasResumed && task.Retries==0 && task.ErrorOutput.Contains("403")) {
      task.Retries++;task.State=TaskState.Queued;task.Progress="刷新视频地址后继续";
      Log(task,"暂停后视频地址失效，自动刷新并续传一次。");continue;
     }
     task.State=TaskState.Failed;task.Progress=LastError(task);
     if(task.AuthenticationFailed){verificationRequired=true;foreach(var waiting in Items.Where(t=>t.Worker==null && t.Ready && t.State==TaskState.Queued))waiting.State=TaskState.Paused;}
     continue;
    }
    try {
     string video=File.ReadAllLines(task.PathList,Encoding.UTF8).LastOrDefault(s=>!String.IsNullOrWhiteSpace(s));
     if(String.IsNullOrEmpty(video) || !File.Exists(video))throw new IOException("下载器没有返回有效的视频文件。");
     task.FilePath=video;task.Title=Path.GetFileNameWithoutExtension(video);BeginProbe(task);
     if(Paused) {task.Suspension.Suspend(task.Worker.Id);task.State=TaskState.Paused;}
    }catch(Exception ex){task.State=TaskState.Failed;task.Progress="下载结束，检查失败："+ex.Message;Log(task,ex.Message);StopWorker(task);}
   }
   FillSlots();
  }
  void FillSlots() {
   if(!Started || Paused || VerificationRequired)return;
   int active=Items.Count(t=>t.Worker!=null);
   foreach(var task in Items.Where(t=>t.Ready && t.State==TaskState.Queued).ToArray()) {
    if(active>=Concurrency || VerificationRequired)break;
    try {BeginDownload(task);active++;}catch(Exception ex){task.State=TaskState.Failed;task.Progress=ex.Message;Log(task,ex.ToString());if(ex is AuthenticationPreparationException){verificationRequired=true;Interlocked.Increment(ref verificationVersion);foreach(var waiting in Items.Where(t=>t.Ready && t.Worker==null && t.State==TaskState.Queued))waiting.State=TaskState.Paused;}try{StopWorker(task);}catch(Exception){task.Progress="任务未启动，身份验证临时文件清理失败。请检查临时目录权限后手动重试。";}}
   }
  }
  void BeginDownload(DownloadTask task) {
   ReleaseLogin(task);
   Directory.CreateDirectory(Path.GetDirectoryName(task.LogPath));
   task.Probing=false;task.ProbeOutput="";task.ErrorOutput="";task.AuthenticationFailed=false;task.State=TaskState.Running;task.Progress="解析视频、下载 / 合并中";
   if(File.Exists(task.PathList))File.Delete(task.PathList);
   if(task.Enhanced){BeginMedia(task);return;}
   var args=new List<string>{"--ignore-config","--no-playlist","--no-simulate","--continue","--newline","--progress","--encoding","utf-8","--socket-timeout","20","--retries","1","--fragment-retries","1","--extractor-retries","1","--js-runtimes","deno:"+Path.Combine(BaseDirectory,"deno.exe"),"--ffmpeg-location",BaseDirectory,"-P",task.DestinationDirectory,"-o",task.OutputTemplate,"-f",VideoQuality.Format(task.TargetHeight),"--merge-output-format","mp4","--remux-video","mp4","--print","before_dl:TITLE:%(title)s","--print-to-file","after_move:%(filepath)s",task.PathList};
   try{task.Login=new LoginSession(Authentication);}catch(ArgumentException){throw new AuthenticationPreparationException();}catch(InvalidOperationException){throw new AuthenticationPreparationException();}
   args.AddRange(task.Login.Arguments);args.AddRange(new[]{"--",task.Url});
   BeginProcess(task,"yt-dlp.exe",args.ToArray());

  }
  void BeginProbe(DownloadTask task) {
   task.Probing=true;task.ProbeOutput="";task.State=TaskState.Running;task.Progress="读取实际分辨率";
   BeginProcess(task,"ffprobe.exe",new[]{"-v","error","-select_streams","v:0","-show_entries","stream=width,height","-of","json",task.FilePath});
  }
  void BeginProcess(DownloadTask task,string executable,string[] args,string input=null) {
   ProcessStartInfo info=new ProcessStartInfo(Path.Combine(BaseDirectory,executable),String.Join(" ",args.Select(QuoteArgument))) {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=BaseDirectory,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false)};
   Process p=new Process();p.StartInfo=info;
   if(input!=null){info.RedirectStandardInput=true;info.EnvironmentVariables["PYTHONUTF8"]="1";}
   p.OutputDataReceived+=(s,e)=>{if(e.Data!=null)Receive(task,e.Data,false);};
   p.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)Receive(task,e.Data,true);};
   task.Worker=p;
   p.Start();p.BeginOutputReadLine();p.BeginErrorReadLine();
   if(input!=null){using(var writer=new StreamWriter(p.StandardInput.BaseStream,new UTF8Encoding(false),1024,true)){writer.Write(input);}p.StandardInput.Close();}
  }
  void Receive(DownloadTask task,string line,bool error) {
   lock(task.Gate) {
    if(task.Enhanced){if(!error)ReceiveMedia(task,line);return;}
    LogUnlocked(task,line);
    if(task.Probing){if(!error)task.ProbeOutput+=line+"\n";return;}
    if(line.StartsWith("TITLE:",StringComparison.Ordinal)){task.Title=line.Substring(6);return;}
    string safe=EngineMessages.SafeLogLine(line);
    if(line.StartsWith("[download]") || line.StartsWith("[Merger]") || line.StartsWith("[VideoRemuxer]"))task.Progress=safe??"正在处理视频";
    if(line.IndexOf("ERROR:",StringComparison.OrdinalIgnoreCase)>=0 || (error && EngineMessages.IsAuthenticationFailure(line) && (line.ToLowerInvariant().Contains("failed") || line.ToLowerInvariant().Contains("could not") || line.ToLowerInvariant().Contains("unable")))){
     bool authentication=EngineMessages.IsAuthenticationFailure(line);
     if(authentication){task.AuthenticationFailed=true;verificationRequired=true;Interlocked.Increment(ref verificationVersion);}
     if(authentication || !task.AuthenticationFailed)task.ErrorOutput+=EngineMessages.Failure(line)+"\n";
    }
   }
  }
  void FinishProbe(DownloadTask task,int result) {
   Match width=Regex.Match(task.ProbeOutput,"\"width\"\\s*:\\s*(\\d+)"),height=Regex.Match(task.ProbeOutput,"\"height\"\\s*:\\s*(\\d+)");
   if(result==0 && width.Success && height.Success) {task.Resolution=width.Groups[1].Value+" × "+height.Groups[1].Value;task.State=TaskState.Completed;task.Progress="完成 · "+height.Groups[1].Value+"p";}
   else {task.State=TaskState.Failed;task.Progress="视频已下载，但无法读取实际分辨率";}
   if(File.Exists(task.PathList))File.Delete(task.PathList);
  }
  static string LastError(DownloadTask task) {
   string[] lines=task.ErrorOutput.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
   string error=lines.LastOrDefault();
   return error ?? "任务失败；可查看日志或重试。";
  }
  public void RetryFailed() {
   ThrowIfDisposed();foreach(var task in Items.Where(t=>t.State==TaskState.Failed).ToArray())Requeue(task);
  }
  public void Requeue(DownloadTask task){
   ThrowIfDisposed();if(!Items.Contains(task) || task.Worker!=null || (task.State!=TaskState.Completed && task.State!=TaskState.Failed))throw new InvalidOperationException("请选择已完成或失败的任务重新下载。");
   if(task.State==TaskState.Completed){task.RedownloadRound++;task.RedownloadKey=Guid.NewGuid().ToString("N");}task.Ready=false;task.State=TaskState.Queued;task.Retries=0;task.WasResumed=false;task.Resolution="";task.Progress="已重新排队，等待手动开始";
  }
  public void RemoveTasks(IEnumerable<DownloadTask> selected){
   ThrowIfDisposed();var tasks=selected.Distinct().ToArray();
   if(tasks.Any(t=>!Items.Contains(t) || t.Worker!=null))throw new InvalidOperationException("正在下载或合并的任务不能删除，请等待任务结束。");
   foreach(var task in tasks)ReleaseLogin(task);
   foreach(var task in tasks)Items.Remove(task);
  }
  public void ClearWaiting(){RemoveTasks(Items.Where(t=>t.Worker==null && (t.State==TaskState.Queued || t.State==TaskState.Paused)).ToArray());}
  public void Pause() {
   ThrowIfDisposed();if(Paused)return;Paused=true;
   try {
    foreach(var task in Items.Where(t=>t.Worker!=null).ToArray()) {
     if(task.Worker.HasExited)continue;
     task.Suspension.Suspend(task.Worker.Id);task.State=TaskState.Paused;
    }
   }catch {Resume();throw;}
  }
  public void Resume() {
   ThrowIfDisposed();
   foreach(var task in Items.Where(t=>t.Worker!=null)) {
    task.Suspension.Resume();task.WasResumed=true;
    if(task.State==TaskState.Paused)task.State=TaskState.Running;
   }
   if(!VerificationRequired)foreach(var task in Items.Where(t=>t.Ready && t.Worker==null && t.State==TaskState.Paused))task.State=TaskState.Queued;
   Paused=false;FillSlots();
  }
  static void Log(DownloadTask task,string line){lock(task.Gate){LogUnlocked(task,line);}}
  static void LogUnlocked(DownloadTask task,string line) {
   string safe=EngineMessages.SafeLogLine(line);if(safe==null)return;
   try {File.AppendAllText(task.LogPath,safe+Environment.NewLine,new UTF8Encoding(false));}catch(IOException){}catch(UnauthorizedAccessException){}
  }
  public static string QuoteArgument(string value) {
   // Windows CommandLineToArgvW: double trailing backslashes and those before quotes.
   string escaped=Regex.Replace(value,@"(\\*)\""",m=>new string('\\',m.Groups[1].Length*2+1)+"\"");
   escaped=Regex.Replace(escaped,@"(\\+)$",m=>new string('\\',m.Groups[1].Length*2));
   return "\""+escaped+"\"";
  }
  void StopWorker(DownloadTask task) {
   if(task.Worker==null){ReleaseLogin(task);return;}
   try {
    if(!task.Worker.HasExited) {
     try {task.Suspension.Suspend(task.Worker.Id);}catch{}
     ProcessSuspension.KillTree(task.Worker.Id);
     task.Suspension.Resume();task.Worker.WaitForExit(3000);
    }
   }catch(InvalidOperationException){}finally{task.Suspension.Resume();task.Worker.Dispose();task.Worker=null;ReleaseLogin(task);}
  }
  static void ReleaseLogin(DownloadTask task){if(task.Login!=null){task.Login.Dispose();task.Login=null;}}
  void ThrowIfDisposed(){if(disposed)throw new ObjectDisposedException("DownloadQueue");}
  public void Dispose(){
   disposed=true;Paused=true;bool failed=false;
   foreach(var task in Items){try{StopWorker(task);}catch(Exception){failed=true;}}
   disposed=true;CleanupWarning=failed?"下载进程已尝试停止，但有身份验证临时文件无法清理。请检查 Windows 临时目录中 YouTube4K-auth- 开头文件夹的权限。":null;
  }
 }

 internal sealed class ProcessSuspension {
  readonly List<IntPtr> handles=new List<IntPtr>();
  [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr handle);
  [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr handle);
  [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint id);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint id);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot,ref Entry entry);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot,ref Entry entry);
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Entry {public uint Size,Usage,Id;public UIntPtr Heap;public uint Module,Threads,Parent;public int Priority;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Exe;}
  static List<int> Children(int id) {
   List<int> result=new List<int>();IntPtr snapshot=CreateToolhelp32Snapshot(2,0);
   if(snapshot==new IntPtr(-1))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
   try {Entry entry=new Entry();entry.Size=(uint)Marshal.SizeOf(typeof(Entry));if(Process32FirstW(snapshot,ref entry))do{if(entry.Parent==(uint)id)result.Add((int)entry.Id);}while(Process32NextW(snapshot,ref entry));}finally{CloseHandle(snapshot);}return result;
  }
  public void Suspend(int id) {if(handles.Count>0)return;try{SuspendRecursive(id);}catch{Resume();throw;}}
  void SuspendRecursive(int id) {
   IntPtr h=OpenProcess(0x0800,false,(uint)id);
   if(h==IntPtr.Zero) {
    int error=Marshal.GetLastWin32Error();if(error==87)return;
    throw new System.ComponentModel.Win32Exception(error,"无法暂停下载进程。");
   }
   int status=NtSuspendProcess(h);
   if(status!=0){CloseHandle(h);if(status==unchecked((int)0xC000010A))return;throw new InvalidOperationException("无法暂停下载进程："+status.ToString("X"));}
   handles.Add(h);
   foreach(int child in Children(id))SuspendRecursive(child);
  }
  public void Resume() {for(int i=handles.Count-1;i>=0;i--){NtResumeProcess(handles[i]);CloseHandle(handles[i]);}handles.Clear();}
  public static void KillTree(int id) {
   foreach(int child in Children(id))KillTree(child);
   try {using(Process p=Process.GetProcessById(id)){if(!p.HasExited)p.Kill();}}catch(ArgumentException){}catch(InvalidOperationException){}
  }
 }
}
