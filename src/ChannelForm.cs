using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YouTube4K {
 public sealed class ChannelForm : Form {
  readonly string root;
  readonly LoginOptions login;
  readonly TextBox url=new TextBox();
  readonly ComboBox type=new ComboBox();
  readonly Button read=new Button(),stop=new Button(),selectAll=new Button(),clear=new Button(),add=new Button(),details=new Button();
  readonly DataGridView grid=new DataGridView();
  readonly Label status=new Label(),selection=new Label();
  readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
  readonly ConcurrentQueue<ChannelVideo> pending=new ConcurrentQueue<ChannelVideo>();
  readonly ConcurrentQueue<string> notices=new ConcurrentQueue<string>();
  readonly ConcurrentQueue<string> failures=new ConcurrentQueue<string>();
  List<string> errors=new List<string>();
  Task<ReadSummary> loading;CancellationTokenSource cancellation;bool closing,selectingBatch;
  public List<ChannelVideo> SelectedVideos {get;private set;}
  public ChannelForm(string directory,LoginOptions authentication=null){
   root=directory;login=authentication??new LoginOptions();SelectedVideos=new List<ChannelVideo>();
   Text="混合批量读取 · 选择视频";ClientSize=new Size(1020,780);MinimumSize=new Size(900,680);StartPosition=FormStartPosition.CenterParent;
   Font=new Font("Microsoft YaHei UI",10);BackColor=Color.FromArgb(245,247,251);AutoScaleMode=AutoScaleMode.Dpi;
   var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=8,Padding=new Padding(24)};
   foreach(var height in new[]{44,30,128,48,56})layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
   layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,36));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,54));
   layout.Controls.Add(new Label {Text="选好视频，再开始下载",Dock=DockStyle.Fill,Font=new Font(Font.FontFamily,20,FontStyle.Bold),ForeColor=Color.FromArgb(28,38,58)},0,0);
   layout.Controls.Add(new Label {Text="每行一个地址，可混贴视频、播放列表和频道。带 list= 的地址读取整个列表。",Dock=DockStyle.Fill,ForeColor=Color.FromArgb(90,100,120)},0,1);
   url.Dock=DockStyle.Fill;url.Multiline=true;url.AcceptsReturn=true;url.ScrollBars=ScrollBars.Vertical;url.Margin=new Padding(0,8,0,8);url.Font=new Font(Font.FontFamily,11);url.AccessibleName="多个视频、播放列表或频道链接，每行一个";layout.Controls.Add(url,0,2);
   var options=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};
   options.Controls.Add(new Label {Text="频道内容",AutoSize=true,Margin=new Padding(0,9,12,0)});
   type.DropDownStyle=ComboBoxStyle.DropDownList;type.Items.AddRange(new object[]{"普通视频","Shorts 短视频","直播回放"});type.SelectedIndex=0;type.Width=150;type.Margin=new Padding(0,5,16,0);options.Controls.Add(type);
   Configure(read,"读取全部地址",true);Configure(stop,"停止读取",false);stop.Enabled=false;options.Controls.Add(read);options.Controls.Add(stop);
   options.Controls.Add(new Label {Text="播放列表按列表内容读取",AutoSize=true,ForeColor=Color.FromArgb(90,100,120),Margin=new Padding(8,10,0,0)});layout.Controls.Add(options,0,3);
   status.Text="读取后统一去重，再勾选或全选。此步骤只读取信息，不下载视频。";status.Dock=DockStyle.Fill;status.ForeColor=Color.FromArgb(70,80,100);status.Padding=new Padding(0,6,0,4);layout.Controls.Add(status,0,4);
   grid.Dock=DockStyle.Fill;grid.BackgroundColor=Color.White;grid.BorderStyle=BorderStyle.None;grid.AllowUserToAddRows=false;grid.AllowUserToDeleteRows=false;grid.RowHeadersVisible=false;grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;grid.MultiSelect=false;grid.RowTemplate.Height=36;grid.ColumnHeadersHeight=38;grid.EnableHeadersVisualStyles=false;
   grid.ColumnHeadersDefaultCellStyle.BackColor=Color.FromArgb(233,238,247);grid.DefaultCellStyle.SelectionBackColor=Color.FromArgb(225,236,255);grid.DefaultCellStyle.SelectionForeColor=Color.Black;grid.AlternatingRowsDefaultCellStyle.BackColor=Color.FromArgb(250,251,253);
   grid.Columns.Add(new DataGridViewCheckBoxColumn {Name="Checked",HeaderText="选择",FillWeight=8});
   grid.Columns.Add(new DataGridViewTextBoxColumn {Name="VideoTitle",HeaderText="视频标题",ReadOnly=true,FillWeight=72});
   grid.Columns.Add(new DataGridViewTextBoxColumn {Name="Duration",HeaderText="时长",ReadOnly=true,FillWeight=12});
   foreach(DataGridViewColumn col in grid.Columns)col.SortMode=DataGridViewColumnSortMode.NotSortable;
   grid.CurrentCellDirtyStateChanged+=(s,e)=>{if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
   grid.CellValueChanged+=SelectionChanged;layout.Controls.Add(grid,0,5);
   selection.Dock=DockStyle.Fill;selection.Padding=new Padding(0,8,0,0);layout.Controls.Add(selection,0,6);
   var footer=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1};footer.RowStyles.Add(new RowStyle(SizeType.Percent,100));footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,210));
   var choices=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};Configure(selectAll,"全选",false);Configure(clear,"取消全选",false);Configure(details,"查看失败地址",false);details.Width=145;details.Enabled=false;choices.Controls.Add(selectAll);choices.Controls.Add(clear);choices.Controls.Add(details);footer.Controls.Add(choices,0,0);
   Configure(add,"加入下载队列",true);add.Dock=DockStyle.Fill;add.Margin=new Padding(0,3,0,7);footer.Controls.Add(add,1,0);layout.Controls.Add(footer,0,7);Controls.Add(layout);
   read.Click+=(s,e)=>LoadChannel();stop.Click+=(s,e)=>{if(cancellation!=null)cancellation.Cancel();stop.Enabled=false;status.Text="正在停止读取…";};
   selectAll.Click+=(s,e)=>SetChecked(true);clear.Click+=(s,e)=>SetChecked(false);
   details.Click+=(s,e)=>{using(var dialog=new Form {Text="未完成的地址",Size=new Size(800,440),StartPosition=FormStartPosition.CenterParent,Font=Font}){
    dialog.Controls.Add(new TextBox {Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,Text=String.Join("\r\n\r\n",errors),BackColor=Color.White});dialog.ShowDialog(this);
   }};
   add.Click+=(s,e)=>{grid.EndEdit();SelectedVideos=grid.Rows.Cast<DataGridViewRow>().Where(r=>Object.Equals(r.Cells[0].Value,true)).Select(r=>(ChannelVideo)r.Tag).ToList();if(SelectedVideos.Count>0){DialogResult=DialogResult.OK;Close();}};
   timer.Interval=100;timer.Tick+=(s,e)=>Drain();timer.Start();
   FormClosing+=(s,e)=>{if(loading!=null){closing=true;cancellation.Cancel();e.Cancel=true;status.Text="正在停止读取并关闭…";}};
   FormClosed+=(s,e)=>{timer.Stop();timer.Dispose();if(cancellation!=null)cancellation.Dispose();};UpdateSelection();
  }
  static void Configure(Button button,string text,bool primary){button.Text=text;button.Size=new Size(124,38);button.Margin=new Padding(0,3,10,0);button.FlatStyle=FlatStyle.Flat;button.BackColor=primary?Color.FromArgb(37,99,235):Color.White;button.ForeColor=primary?Color.White:Color.FromArgb(45,55,75);button.FlatAppearance.BorderColor=primary?button.BackColor:Color.FromArgb(213,221,234);button.Cursor=Cursors.Hand;}
  void SetChecked(bool value){grid.EndEdit();selectingBatch=true;try{foreach(DataGridViewRow row in grid.Rows)row.Cells[0].Value=value;}finally{selectingBatch=false;}UpdateSelection();}
  void SelectionChanged(object sender,DataGridViewCellEventArgs e){if(!selectingBatch)UpdateSelection();}
  void UpdateSelection(){int count=grid.Rows.Cast<DataGridViewRow>().Count(r=>Object.Equals(r.Cells[0].Value,true));selection.Text=String.Format("已读取 {0} 个视频 · 已选 {1} 个",grid.Rows.Count,count);add.Enabled=loading==null && count>0;selectAll.Enabled=clear.Enabled=loading==null && grid.Rows.Count>0;}
  void LoadChannel(){
   string input=url.Text,tab=new[]{"videos","shorts","streams"}[type.SelectedIndex];
   if(String.IsNullOrWhiteSpace(input)){status.Text="请粘贴至少一个链接，每行一个。";return;}
   grid.Rows.Clear();ChannelVideo ignored;while(pending.TryDequeue(out ignored)){}
   string oldNotice;while(notices.TryDequeue(out oldNotice)){}while(failures.TryDequeue(out oldNotice)){}errors.Clear();details.Enabled=false;
   if(cancellation!=null)cancellation.Dispose();cancellation=new CancellationTokenSource();var token=cancellation.Token;
   read.Enabled=false;url.Enabled=false;type.Enabled=false;stop.Enabled=true;status.Text="正在读取列表，可随时停止；视频较多时需要一些时间…";
   loading=Task.Run(()=>new ChannelReader(root,login).ReadMany(input,tab,pending.Enqueue,notices.Enqueue,token,failures.Enqueue));UpdateSelection();
  }
  void Drain(){
   if(closing && loading!=null && loading.IsCompleted){var ignored=loading.Exception;loading=null;Close();return;}
   string notice;while(notices.TryDequeue(out notice)){if(loading!=null && !cancellation.IsCancellationRequested)status.Text=notice;}
   ChannelVideo video;int count=0;bool changed=false;selectingBatch=true;
   try{while(count++<200 && pending.TryDequeue(out video)){int row=grid.Rows.Add(false,video.Title,video.DurationText);grid.Rows[row].Tag=video;changed=true;}}
   finally{selectingBatch=false;}
   if(loading!=null && loading.IsCompleted && pending.IsEmpty){
    string failure;while(failures.TryDequeue(out failure))errors.Add(failure);details.Enabled=errors.Count>0;
    var finished=loading;loading=null;read.Enabled=true;url.Enabled=true;type.Enabled=true;stop.Enabled=false;changed=true;
    if(cancellation.IsCancellationRequested)status.Text="已停止读取，下面是已读取的部分视频，可勾选后加入队列。";
    else if(finished.IsFaulted)status.Text="读取未完成，已保留部分结果。"+finished.Exception.GetBaseException().Message;
    else {
     var result=finished.Result;errors=result.Errors;details.Enabled=errors.Count>0;
     if(errors.Count>0)status.Text=String.Format("读取未完成：{0} 个地址成功，{1} 个地址出错。已保留结果，请查看失败地址。",result.Completed,errors.Count);
     else status.Text=grid.Rows.Count==0?"未找到可下载的视频，请检查链接、内容类型或访问权限。":String.Format("读取完成：{0} 个地址成功，跳过 {1} 个重复地址。勾选或全选后加入队列。",result.Completed,result.DuplicateSources);
    }
    var observed=finished.Exception;
   }
   if(changed)UpdateSelection();
  }
 }
}
