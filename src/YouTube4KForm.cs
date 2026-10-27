using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YouTube4K {
 static class Program {
  [STAThread] static void Main() {
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   string root=AppDomain.CurrentDomain.BaseDirectory;
   string key;using(SHA256 sha=SHA256.Create()){key=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()))).Replace("-","");}
   using(Mutex mutex=new Mutex(false,"Local\\YouTube4K-"+key)) {
    bool owned=false;
    try {try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
     if(!owned){MessageBox.Show("这个文件夹里的下载器已经打开，请使用现有窗口。","YouTube 4K 下载器");return;}
     Application.Run(new DownloaderForm(root));
    }finally{if(owned)mutex.ReleaseMutex();}
   }
  }
 }
 public sealed class DownloaderForm : Form {
  readonly DownloadQueue queue;
  readonly TextBox links=new TextBox();
  readonly DataGridView grid=new DataGridView();
  readonly Button start=new Button(),pause=new Button(),resume=new Button(),retry=new Button(),folder=new Button();
  readonly Label summary=new Label();
  readonly TextBox savePath=new TextBox();
  readonly Button chooseFolder=new Button();
  readonly Button channel=new Button();
  readonly Button addLinks=new Button(),remove=new Button(),clearWaiting=new Button(),retrySelected=new Button();
  readonly NumericUpDown concurrency=new NumericUpDown();
  readonly ComboBox quality=new ComboBox();
  readonly CheckBox wantVideo=new CheckBox(),wantAudio=new CheckBox(),wantTranscript=new CheckBox();
  readonly ComboBox transcriptLanguage=new ComboBox(),transcriptSource=new ComboBox();
  readonly Button openAudio=new Button(),openTranscript=new Button();
  bool preparing,closeAfterPreparation,verificationShown;
  CancellationTokenSource preparationCancel;
  internal Func<bool> ExitConfirmation;
  readonly ComboBox authMode=new ComboBox();
  readonly TextBox cookiePath=new TextBox();
  readonly Button chooseCookie=new Button(),testConnection=new Button();
  readonly Label authStatus=new Label();
  CancellationTokenSource testCancellation;Task<ConnectionResult> connectionTest;
  bool closingAfterTest;
  readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
  public DownloaderForm(string root) {
   queue=new DownloadQueue(root);
   ExitConfirmation=()=>MessageBox.Show("还有未完成的任务。退出会停止当前下载，已有的下载片段会保留。确定退出？","退出下载器",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes;
   Text="YouTube 4K 素材助手 · 视频 / 音频 / 文字稿";ClientSize=new Size(1100,980);MinimumSize=new Size(900,898);StartPosition=FormStartPosition.CenterScreen;
   Font=new Font("Microsoft YaHei UI",10);BackColor=Color.FromArgb(245,247,251);AutoScaleMode=AutoScaleMode.Dpi;
   TableLayoutPanel layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=12,Padding=new Padding(24)};
   layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,32));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,50));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,66));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,35));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
   layout.RowStyles.Insert(3,new RowStyle(SizeType.Absolute,48));
   layout.RowStyles.Insert(4,new RowStyle(SizeType.Absolute,100));
   layout.RowStyles.Insert(4,new RowStyle(SizeType.Absolute,50));
   layout.RowStyles.Insert(5,new RowStyle(SizeType.Absolute,70));
   Label title=new Label {Text="YouTube 4K 批量下载",AutoSize=true,Font=new Font(Font.FontFamily,22,FontStyle.Bold),ForeColor=Color.FromArgb(28,38,58),Dock=DockStyle.Fill};layout.Controls.Add(title,0,0);
   layout.Controls.Add(new Label {Text="先收集链接、检查列表，再手动点击“开始全部下载”。默认逐个下载。",Dock=DockStyle.Fill,ForeColor=Color.FromArgb(90,100,120)},0,1);
   links.Multiline=true;links.ScrollBars=ScrollBars.Vertical;links.Dock=DockStyle.Fill;links.Font=new Font("Microsoft YaHei UI",11);links.BackColor=Color.White;links.BorderStyle=BorderStyle.FixedSingle;links.AcceptsReturn=true;layout.Controls.Add(links,0,2);
   var channelRow=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};
   Configure(channel,"混合批量读取",Color.White,Color.FromArgb(37,99,235));channel.Width=180;
   Configure(addLinks,"添加到列表",Color.FromArgb(37,99,235),Color.White);addLinks.Width=145;
   channelRow.Controls.Add(addLinks);channelRow.Controls.Add(channel);channelRow.Controls.Add(new Label {Text="每行一个；添加和混合读取均只收集任务",AutoSize=true,Margin=new Padding(8,9,0,0),ForeColor=Color.FromArgb(90,100,120)});layout.Controls.Add(channelRow,0,3);
   var manage=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};
   Configure(remove,"删除选中",Color.White,Color.FromArgb(45,55,75));remove.Width=100;
   Configure(clearWaiting,"清空待下载",Color.White,Color.FromArgb(45,55,75));clearWaiting.Width=120;
   Configure(retrySelected,"重下选中",Color.White,Color.FromArgb(45,55,75));retrySelected.Width=110;
   manage.Controls.AddRange(new Control[]{remove,clearWaiting,retrySelected});
   manage.Controls.Add(new Label {Text="同时下载",AutoSize=true,Margin=new Padding(2,10,8,0)});
   concurrency.Minimum=1;concurrency.Maximum=3;concurrency.Value=1;concurrency.Width=52;concurrency.Margin=new Padding(0,6,14,0);manage.Controls.Add(concurrency);
   manage.Controls.Add(new Label {Text="新任务清晰度",AutoSize=true,Margin=new Padding(0,10,8,0)});
   quality.DropDownStyle=ComboBoxStyle.DropDownList;quality.Items.AddRange(VideoQuality.Labels);quality.SelectedIndex=Array.IndexOf(VideoQuality.Heights,2160);quality.Width=175;quality.Margin=new Padding(0,6,0,0);manage.Controls.Add(quality);layout.Controls.Add(manage,0,4);
   var contentPanel=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=new Padding(0)};
   contentPanel.RowStyles.Add(new RowStyle(SizeType.Absolute,42));contentPanel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
   var outputRow=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false,Margin=new Padding(0)};
   outputRow.Controls.Add(new Label {Text="新任务内容",AutoSize=true,Margin=new Padding(0,10,10,0)});
   wantVideo.Text="视频";wantVideo.Checked=true;wantAudio.Text="音频 MP3";wantTranscript.Text="文字稿";
   foreach(var check in new[]{wantVideo,wantAudio,wantTranscript}){check.AutoSize=true;check.Margin=new Padding(0,9,14,0);outputRow.Controls.Add(check);}
   outputRow.Controls.Add(new Label {Text="原话语言",AutoSize=true,Margin=new Padding(2,10,7,0)});
   transcriptLanguage.DropDownStyle=ComboBoxStyle.DropDownList;transcriptLanguage.Items.Add("中文（固定）");transcriptLanguage.SelectedIndex=0;transcriptLanguage.Enabled=false;transcriptLanguage.Width=115;transcriptLanguage.Margin=new Padding(0,6,12,0);outputRow.Controls.Add(transcriptLanguage);
   transcriptSource.DropDownStyle=ComboBoxStyle.DropDownList;transcriptSource.Items.AddRange(new object[]{"字幕优先，无字幕则识别","始终本地语音识别"});transcriptSource.SelectedIndex=0;transcriptSource.Width=205;transcriptSource.Margin=new Padding(0,6,0,0);outputRow.Controls.Add(transcriptSource);
   contentPanel.Controls.Add(outputRow,0,0);contentPanel.Controls.Add(new Label {Text="固定中文识别，保留原话，输出 TXT 和带时间戳的 Markdown；本地识别逐个进行。",Dock=DockStyle.Fill,ForeColor=Color.FromArgb(90,100,120),Font=new Font(Font.FontFamily,9)},0,1);layout.Controls.Add(contentPanel,0,5);
   var authentication=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=5,RowCount=2,Padding=new Padding(0,5,0,0)};
   authentication.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,112));authentication.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,172));authentication.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,136));authentication.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));authentication.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,132));
   authentication.RowStyles.Add(new RowStyle(SizeType.Absolute,44));authentication.RowStyles.Add(new RowStyle(SizeType.Percent,100));
   authentication.Controls.Add(new Label {Text="登录验证方式",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,0);
   authMode.DropDownStyle=ComboBoxStyle.DropDownList;authMode.Items.AddRange(new object[]{"不使用 Cookie","Chrome","Edge","Cookie 文件"});authMode.SelectedIndex=0;authMode.Dock=DockStyle.Fill;authMode.Margin=new Padding(0,7,10,0);authentication.Controls.Add(authMode,1,0);
   Configure(chooseCookie,"选择文件…",Color.White,Color.FromArgb(45,55,75));chooseCookie.Dock=DockStyle.Fill;chooseCookie.Margin=new Padding(0,2,10,4);chooseCookie.Enabled=false;authentication.Controls.Add(chooseCookie,2,0);
   cookiePath.ReadOnly=true;cookiePath.Dock=DockStyle.Fill;cookiePath.Margin=new Padding(0,7,10,0);cookiePath.Enabled=false;authentication.Controls.Add(cookiePath,3,0);
   Configure(testConnection,"测试连接",Color.White,Color.FromArgb(37,99,235));testConnection.Dock=DockStyle.Fill;testConnection.Margin=new Padding(0,2,0,4);authentication.Controls.Add(testConnection,4,0);
   authStatus.Text="默认不读取 Cookie。验证方式仅用于本次运行；测试连接只获取第一条视频的信息。";authStatus.Dock=DockStyle.Fill;authStatus.ForeColor=Color.FromArgb(90,100,120);authStatus.Padding=new Padding(0,4,0,0);authentication.Controls.Add(authStatus,0,1);authentication.SetColumnSpan(authStatus,5);layout.Controls.Add(authentication,0,6);
   TableLayoutPanel destination=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Padding=new Padding(0,10,0,0)};
   destination.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,86));destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));destination.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,145));
   destination.Controls.Add(new Label {Text="保存位置",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,0);
   savePath.ReadOnly=true;savePath.Dock=DockStyle.Fill;savePath.Text=queue.DownloadsDirectory;savePath.BackColor=Color.White;savePath.Margin=new Padding(0,5,10,0);destination.Controls.Add(savePath,1,0);
   Configure(chooseFolder,"选择文件夹…",Color.White,Color.FromArgb(45,55,75));chooseFolder.Dock=DockStyle.Fill;chooseFolder.Margin=new Padding(0);destination.Controls.Add(chooseFolder,2,0);layout.Controls.Add(destination,0,7);
   FlowLayoutPanel buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(0,13,0,0),WrapContents=false};
   Configure(start,"开始全部下载",Color.FromArgb(37,99,235),Color.White);start.Width=150;Configure(pause,"暂停全部",Color.FromArgb(255,237,196),Color.FromArgb(122,77,0));pause.Width=105;Configure(resume,"继续下载",Color.FromArgb(220,245,231),Color.FromArgb(20,105,58));resume.Width=105;Configure(retry,"重试失败",Color.White,Color.FromArgb(45,55,75));retry.Width=105;Configure(folder,"打开视频文件夹",Color.White,Color.FromArgb(45,55,75));folder.Width=155;
   buttons.Controls.AddRange(new Control[]{start,pause,resume,retry,folder});layout.Controls.Add(buttons,0,8);
   summary.Dock=DockStyle.Fill;summary.ForeColor=Color.FromArgb(60,70,90);layout.Controls.Add(summary,0,9);
   grid.Dock=DockStyle.Fill;grid.BackgroundColor=Color.White;grid.BorderStyle=BorderStyle.None;grid.ReadOnly=true;grid.AllowUserToAddRows=false;grid.AllowUserToDeleteRows=false;grid.RowHeadersVisible=false;grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;grid.MultiSelect=false;grid.AutoGenerateColumns=false;grid.RowTemplate.Height=34;grid.ColumnHeadersHeight=38;grid.EnableHeadersVisualStyles=false;grid.ColumnHeadersDefaultCellStyle.BackColor=Color.FromArgb(233,238,247);grid.ColumnHeadersDefaultCellStyle.ForeColor=Color.FromArgb(50,60,80);grid.DefaultCellStyle.SelectionBackColor=Color.FromArgb(225,236,255);grid.DefaultCellStyle.SelectionForeColor=Color.Black;grid.AlternatingRowsDefaultCellStyle.BackColor=Color.FromArgb(250,251,253);
   grid.Columns.Add(new DataGridViewTextBoxColumn {Name="Video",HeaderText="视频",FillWeight=18,MinimumWidth=100});grid.Columns.Add(new DataGridViewTextBoxColumn {Name="Url",HeaderText="视频链接",FillWeight=25,MinimumWidth=210});grid.Columns.Add(new DataGridViewTextBoxColumn {Name="State",HeaderText="状态",FillWeight=9,MinimumWidth=75});grid.Columns.Add(new DataGridViewTextBoxColumn {Name="Quality",HeaderText="目标清晰度",FillWeight=13,MinimumWidth=105});grid.Columns.Add(new DataGridViewTextBoxColumn {Name="Progress",HeaderText="进度 / 提示",FillWeight=28,MinimumWidth=120});grid.Columns.Add(new DataGridViewTextBoxColumn {Name="Resolution",HeaderText="实际分辨率",FillWeight=14,MinimumWidth=115});grid.Columns.Add(new DataGridViewTextBoxColumn {Name="Destination",HeaderText="保存位置",FillWeight=20,MinimumWidth=130});
   foreach(DataGridViewColumn column in grid.Columns)column.SortMode=DataGridViewColumnSortMode.NotSortable;
   layout.Controls.Add(grid,0,10);
   layout.Controls.Add(new Label {Text="清晰度和保存位置只影响新任务；列表可横向滚动查看。双击已完成任务打开视频。",Dock=DockStyle.Fill,Padding=new Padding(0,10,0,0),ForeColor=Color.FromArgb(100,110,128),Font=new Font(Font.FontFamily,9)},0,11);
   var footer=layout.GetControlFromPosition(0,11);if(footer!=null){layout.Controls.Remove(footer);footer.Dispose();}
   layout.RowStyles[11].Height=44;
   var results=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false,Padding=new Padding(0,3,0,0),Margin=new Padding(0)};
   Configure(openAudio,"打开选中音频",Color.White,Color.FromArgb(37,99,235));openAudio.Width=145;
   Configure(openTranscript,"打开选中文字稿",Color.White,Color.FromArgb(37,99,235));openTranscript.Width=160;
   results.Controls.AddRange(new Control[]{openAudio,openTranscript,new Label{Text="选项只影响新任务；双击任务打开首个成果。",AutoSize=true,Margin=new Padding(4,10,0,0),ForeColor=Color.FromArgb(90,100,120)}});layout.Controls.Add(results,0,11);
   grid.Columns.Insert(2,new DataGridViewTextBoxColumn {Name="Content",HeaderText="获取内容",FillWeight=16,MinimumWidth=120,SortMode=DataGridViewColumnSortMode.NotSortable});
   folder.Text="打开保存文件夹";
   Controls.Add(layout);
   authMode.SelectedIndexChanged+=(s,e)=>{chooseCookie.Enabled=cookiePath.Enabled=authMode.SelectedIndex==3;var selected=CurrentLogin();if(selected.Mode==LoginMode.CookieFile && !File.Exists(selected.CookiePath)){authStatus.Text="请选择 Cookie 文件后应用新方式；排队任务暂时沿用之前的验证方式。";return;}queue.Authentication=selected;authStatus.Text="验证方式已更改，将用于后续读取、待启动任务和手动重试。正在下载的任务继续使用原方式。";};
   chooseCookie.Click+=(s,e)=>{using(var dialog=new OpenFileDialog {Title="选择本地 Netscape 格式 Cookie 文件",Filter="Cookie 文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",CheckFileExists=true}){if(dialog.ShowDialog(this)==DialogResult.OK){cookiePath.Text=dialog.FileName;queue.Authentication=CurrentLogin();}}};
   testConnection.Click+=(s,e)=>Run(()=>BeginConnectionTest());
   channel.Click+=(s,e)=>Run(()=>{ApplyOutputs();var login=CurrentLogin();login.Arguments();queue.Authentication=login;using(var picker=new ChannelForm(queue.BaseDirectory,queue.Authentication)){
    if(picker.ShowDialog(this)!=DialogResult.OK)return;
    int added=queue.AddLinks(String.Join("\n",picker.SelectedVideos.Select(v=>v.Url)));
    foreach(var video in picker.SelectedVideos){var task=queue.Items.FirstOrDefault(t=>t.VideoId==video.Id);if(task!=null && task.Title==task.VideoId)task.Title=video.Title;}
    if(added==0){MessageBox.Show("选中的视频已在下载队列中。","无需重复添加");return;}
    RefreshTasks();
   }});
   chooseFolder.Click+=(s,e)=>Run(()=>{using(FolderBrowserDialog dialog=new FolderBrowserDialog {Description="选择视频保存文件夹（会记住此位置）",SelectedPath=queue.DownloadsDirectory,ShowNewFolderButton=true}){if(dialog.ShowDialog(this)!=DialogResult.OK)return;queue.SetDownloadsDirectory(dialog.SelectedPath);savePath.Text=queue.DownloadsDirectory;}});
   addLinks.Click+=(s,e)=>Run(()=>{ApplyOutputs();int added=queue.AddLinks(links.Text);links.Clear();authStatus.Text=String.Format("已添加 {0} 个新任务。检查列表后，手动点击“开始全部下载”。",added);RefreshTasks();});
   openAudio.Click+=(s,e)=>Run(()=>OpenSelected(false));openTranscript.Click+=(s,e)=>Run(()=>OpenSelected(true));
   concurrency.ValueChanged+=(s,e)=>Run(()=>{queue.SetConcurrency((int)concurrency.Value);RefreshTasks();});
   quality.SelectedIndexChanged+=(s,e)=>queue.SetTargetHeight(VideoQuality.Heights[quality.SelectedIndex]);
   remove.Click+=(s,e)=>Run(()=>{if(grid.CurrentRow==null)return;queue.RemoveTasks(new[]{(DownloadTask)grid.CurrentRow.Tag});RefreshTasks();});
   clearWaiting.Click+=(s,e)=>Run(()=>{queue.ClearWaiting();RefreshTasks();});
   retrySelected.Click+=(s,e)=>Run(()=>{if(grid.CurrentRow==null)return;queue.Requeue((DownloadTask)grid.CurrentRow.Tag);RefreshTasks();});
   start.Click+=async (s,e)=>{
    if(preparing)return;
    try{
     var login=CurrentLogin();login.Arguments();queue.Authentication=login;
     if(queue.WaitingCount==0)throw new InvalidOperationException("没有待下载任务，请先点击“添加到列表”。");
     preparing=true;preparationCancel=new CancellationTokenSource();authStatus.Text="正在检查任务、保存目录和本地下载工具…";RefreshTasks();
     int verificationVersion=queue.VerificationVersion;
     await Task.Run(()=>queue.ValidateStart(preparationCancel.Token));
     if(queue.VerificationVersion!=verificationVersion)throw new InvalidOperationException("检查期间出现新的登录验证错误，已保留暂停状态。请处理验证后再次开始。");
     if(!closeAfterPreparation){queue.StartValidated();verificationShown=false;authStatus.Text="已按列表开始下载。低并发减少请求密度，不保证避免网站验证。";}
    }catch(OperationCanceledException){}catch(Exception ex){if(!closeAfterPreparation)MessageBox.Show(ex.Message,"下载前检查",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    finally{preparing=false;if(preparationCancel!=null){preparationCancel.Dispose();preparationCancel=null;}bool requestedClose=closeAfterPreparation;closeAfterPreparation=false;if(requestedClose)Close();if(!IsDisposed)RefreshTasks();}
   };
   pause.Click+=(s,e)=>Run(()=>{queue.Pause();RefreshTasks();});resume.Click+=(s,e)=>Run(()=>{queue.Resume();RefreshTasks();});retry.Click+=(s,e)=>Run(()=>{var login=CurrentLogin();login.Arguments();queue.Authentication=login;queue.RetryFailed();RefreshTasks();});
   folder.Click+=(s,e)=>Run(()=>{Directory.CreateDirectory(queue.DownloadsDirectory);Process.Start("explorer.exe",DownloadQueue.QuoteArgument(queue.DownloadsDirectory));});
   grid.CellDoubleClick+=(s,e)=>{if(e.RowIndex<0 || e.RowIndex>=grid.Rows.Count)return;var task=(DownloadTask)grid.Rows[e.RowIndex].Tag;Run(()=>{string path=task.State==TaskState.Completed?task.FilePath:task.LogPath;if(!String.IsNullOrEmpty(path) && File.Exists(path))Process.Start(new ProcessStartInfo(path){UseShellExecute=true});});};
   timer.Interval=500;timer.Tick+=(s,e)=>Run(()=>{FinishConnectionTest();if(!closingAfterTest && !queue.IsDisposed){queue.Tick();RefreshTasks();}});timer.Start();
   FormClosing+=(s,e)=>{if(preparing){closeAfterPreparation=true;preparationCancel.Cancel();e.Cancel=true;return;}if(closingAfterTest){e.Cancel=connectionTest!=null;return;}if(queue.Started && queue.HasUnfinished && !ExitConfirmation()){e.Cancel=true;return;}if(connectionTest!=null){testCancellation.Cancel();closingAfterTest=true;e.Cancel=true;queue.Dispose();return;}queue.Dispose();};
   FormClosed+=(s,e)=>{timer.Stop();timer.Dispose();if(testCancellation!=null)testCancellation.Dispose();queue.Dispose();if(queue.CleanupWarning!=null)MessageBox.Show(queue.CleanupWarning,"清理提示",MessageBoxButtons.OK,MessageBoxIcon.Warning);};
   RefreshTasks();
  }
  LoginOptions CurrentLogin(){return new LoginOptions((LoginMode)authMode.SelectedIndex,cookiePath.Text);}
  void ApplyOutputs(){var value=(wantVideo.Checked?MediaOutputs.Video:0)|(wantAudio.Checked?MediaOutputs.Audio:0)|(wantTranscript.Checked?MediaOutputs.Transcript:0);queue.SetOutputs(value,"zh",transcriptSource.SelectedIndex==0?"auto":"whisper");}
  void OpenSelected(bool transcript){var task=grid.CurrentRow==null?null:grid.CurrentRow.Tag as DownloadTask;string path=task==null?null:transcript?task.TranscriptPath:task.AudioPath;if(!String.IsNullOrEmpty(path) && File.Exists(path))Process.Start(new ProcessStartInfo(path){UseShellExecute=true});else MessageBox.Show("选中任务还没有生成这类文件。","打开成果");}
  void BeginConnectionTest(){
   if(connectionTest!=null){testCancellation.Cancel();testConnection.Enabled=false;authStatus.Text="正在停止测试…";return;}
   string first=links.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
   if(String.IsNullOrWhiteSpace(first)){var selected=grid.CurrentRow;first=selected!=null && selected.Index<queue.Items.Count?queue.Items[selected.Index].Url:queue.Items.Select(t=>t.Url).FirstOrDefault();}
   if(String.IsNullOrWhiteSpace(first)){authStatus.Text="请先在输入框粘贴一个视频链接，再测试连接。";return;}
   var login=CurrentLogin();login.Arguments();if(testCancellation!=null)testCancellation.Dispose();testCancellation=new CancellationTokenSource();var token=testCancellation.Token;
   authMode.Enabled=chooseCookie.Enabled=cookiePath.Enabled=false;testConnection.Text="停止测试";authStatus.Text="正在测试连接，只读取视频信息，最多等待 60 秒…";
   connectionTest=Task.Run(()=>new ConnectionTester(queue.BaseDirectory).Test(first,login,token));
  }
  void FinishConnectionTest(){
   if(connectionTest==null || !connectionTest.IsCompleted)return;var finished=connectionTest;connectionTest=null;
   authMode.Enabled=true;chooseCookie.Enabled=cookiePath.Enabled=authMode.SelectedIndex==3;testConnection.Enabled=true;testConnection.Text="测试连接";
   if(testCancellation.IsCancellationRequested)authStatus.Text="测试已停止。";
   else if(finished.IsFaulted)authStatus.Text="测试无法启动，请检查视频链接和本地工具是否完整。";
   else {authStatus.Text=finished.Result.Message;authStatus.ForeColor=finished.Result.Success?Color.FromArgb(20,120,65):Color.FromArgb(190,45,45);}
   var observed=finished.Exception;if(closingAfterTest)Close();
  }
  void Configure(Button button,string text,Color background,Color foreground){button.Text=text;button.Size=new Size(125,39);button.Margin=new Padding(0,0,10,0);button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderColor=background;button.BackColor=background;button.ForeColor=foreground;button.Cursor=Cursors.Hand;}
  void Run(Action action){try{action();}catch(Exception ex){MessageBox.Show(ex.Message,"操作提示",MessageBoxButtons.OK,MessageBoxIcon.Information);}}
  void RefreshTasks() {
   DownloadTask selected=grid.CurrentRow==null?null:grid.CurrentRow.Tag as DownloadTask;
   if(grid.Rows.Count!=queue.Items.Count || grid.Rows.Cast<DataGridViewRow>().Where((r,i)=>!Object.ReferenceEquals(r.Tag,queue.Items[i])).Any()){
    grid.Rows.Clear();foreach(var task in queue.Items){int index=grid.Rows.Add();grid.Rows[index].Tag=task;}
    if(selected!=null){var row=grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r=>Object.ReferenceEquals(r.Tag,selected));if(row!=null)grid.CurrentCell=row.Cells[0];}
   }
   foreach(DataGridViewRow row in grid.Rows) {
    var task=(DownloadTask)row.Tag;
    row.Cells["Content"].Value=task.ContentText;row.Cells["Content"].ToolTipText=task.ContentText+"；原话语言："+task.Language;
    row.Cells["Video"].Value=task.Title;row.Cells["Url"].Value=task.Url;row.Cells["State"].Value=StateText(task.State);row.Cells["Quality"].Value=task.QualityText;row.Cells["Progress"].Value=task.State==TaskState.Paused && task.Worker!=null?"已暂停，点击继续下载":task.Progress;row.Cells["Resolution"].Value=task.Resolution;row.Cells["Destination"].Value=task.DestinationDirectory;
    row.Cells["Progress"].ToolTipText=task.Progress;row.Cells["Url"].ToolTipText=task.Url;row.Cells["Destination"].ToolTipText=task.DestinationDirectory;
    row.Cells["State"].Style.ForeColor=task.State==TaskState.Completed?Color.FromArgb(20,120,65):task.State==TaskState.Failed?Color.FromArgb(190,45,45):Color.FromArgb(50,70,100);
   }
   int running=queue.Items.Count(t=>t.State==TaskState.Running),done=queue.Items.Count(t=>t.State==TaskState.Completed),failed=queue.Items.Count(t=>t.State==TaskState.Failed),waiting=queue.WaitingCount;
   summary.Text=(queue.VerificationRequired?"验证暂停：未启动的新任务已暂停":queue.Paused?"已暂停全部任务":"同时下载上限："+queue.Concurrency)+"    总任务 "+queue.Items.Count+"    进行中 "+running+"    待下载 "+waiting+"    完成 "+done+"    失败 "+failed;
   if(queue.VerificationRequired && !verificationShown){verificationShown=true;authStatus.ForeColor=Color.FromArgb(190,45,45);authStatus.Text="已暂停启动新任务。请检查登录验证方式，测试后手动点击“开始全部下载”；软件不会自动关闭浏览器。";}
   pause.Enabled=queue.Items.Any(t=>t.Worker!=null) && !queue.Paused && !preparing;resume.Enabled=queue.Paused && !preparing;retry.Enabled=failed>0 && !preparing;
   start.Enabled=!queue.Paused && !preparing && connectionTest==null;start.Text=preparing?"正在检查…":"开始全部下载";
   channel.Enabled=addLinks.Enabled=remove.Enabled=clearWaiting.Enabled=retrySelected.Enabled=chooseFolder.Enabled=concurrency.Enabled=quality.Enabled=!preparing;
   wantVideo.Enabled=wantAudio.Enabled=wantTranscript.Enabled=!preparing;transcriptLanguage.Enabled=false;transcriptSource.Enabled=!preparing && wantTranscript.Checked;
   authMode.Enabled=!preparing && connectionTest==null;chooseCookie.Enabled=cookiePath.Enabled=!preparing && connectionTest==null && authMode.SelectedIndex==3;testConnection.Enabled=!preparing;
  }
  static string StateText(TaskState state){switch(state){case TaskState.Queued:return "等待中";case TaskState.Running:return "下载中";case TaskState.Paused:return "已暂停";case TaskState.Completed:return "已完成";default:return "失败";}}
 }
}
