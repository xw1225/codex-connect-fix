using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace RemoteHelper {
    public sealed class MainWindow : Form {
        private static readonly Color Ink=Color.FromArgb(25,39,61), Muted=Color.FromArgb(95,109,130), Blue=Color.FromArgb(37,99,235), Green=Color.FromArgb(18,116,77), Amber=Color.FromArgb(150,91,20);
        private readonly Panel viewport=new Panel(), remote=new Panel(), reconnect=new Panel();
        private readonly Label heading=new Label(), subtitle=new Label(), remoteHeading=new Label(), retryHeading=new Label(), remoteStatus=new Label(), retryStatus=new Label(), remoteDetail=new Label(), retryDetail=new Label(), remoteHint=new Label(), retryHint=new Label(), proxyInfo=new Label(), proxyCaption=new Label(), proxyHint=new Label(), operation=new Label(), footer=new Label(), version=new Label();
        private readonly Button deploy=new Button(), uninstall=new Button(), repair=new Button(), restore=new Button(), refresh=new Button();
        private readonly LinkLabel proxySettings=new LinkLabel(), detailsToggle=new LinkLabel();
        private readonly TextBox proxy=new TextBox(), details=new TextBox();
        private readonly ProgressBar progress=new ProgressBar();
        private readonly float dpi;
        private readonly bool autoDetect;
        private bool busy,layingOut,proxyExpanded,detailsExpanded;
        private StatusView remoteView,retryView;
        private string relayReport="转发器尚未检测。",retryReport="重连配置尚未检测。",proxyReport="代理尚未检测。";
        private int Px(float value) { return (int)Math.Round(value*dpi); }
        private void StyleLabel(Label label,string name,string text,float size,bool bold,Color color) {
            label.Name=name; label.Text=text; label.Font=new Font("Microsoft YaHei UI",size,bold ? FontStyle.Bold : FontStyle.Regular);
            label.ForeColor=color; label.AutoSize=false; label.UseMnemonic=false; label.TextAlign=ContentAlignment.TopLeft;
        }
        private void StyleButton(Button button,string name,string text,bool primary) {
            button.Name=name; button.Text=text; button.Font=new Font("Microsoft YaHei UI",10);
            button.FlatStyle=FlatStyle.Flat; button.FlatAppearance.BorderSize=1;
            button.FlatAppearance.BorderColor=primary ? Blue : Color.FromArgb(207,215,226);
            button.BackColor=primary ? Blue : Color.White; button.ForeColor=primary ? Color.White : Ink;
            button.UseVisualStyleBackColor=false; button.Cursor=Cursors.Hand;
        }
        private void StyleLink(LinkLabel link,string name,string text) {
            link.Name=name; link.Text=text; link.Font=new Font("Microsoft YaHei UI",9); link.LinkColor=Blue; link.ActiveLinkColor=Blue;
            link.LinkBehavior=LinkBehavior.HoverUnderline; link.AutoSize=false;
        }
        public MainWindow() : this(true) { }
        public MainWindow(bool autoDetect) {
            this.autoDetect=autoDetect;
            // One scaling path: measure text at the real device DPI, scale geometry
            // once. Fixed AutoScale rows previously diverged from point-sized fonts.
            AutoScaleMode=AutoScaleMode.None;
            using (var graphics=CreateGraphics()) dpi=graphics.DpiX/96f;
            Name="mainWindow"; Text="Codex 连接修复"; BackColor=Color.FromArgb(244,247,251);
            Font=new Font("Microsoft YaHei UI",9.5f); StartPosition=FormStartPosition.CenterScreen;
            var area=Screen.FromControl(this).WorkingArea;
            ClientSize=new Size(Math.Min(Px(760),area.Width-Px(48)),Math.Min(Px(650),area.Height-Px(70)));
            MinimumSize=new Size(Math.Min(Px(520),area.Width-Px(24)),Math.Min(Px(470),area.Height-Px(40)));
            viewport.Name="viewport"; viewport.Dock=DockStyle.Fill; viewport.AutoScroll=true; Controls.Add(viewport);
            foreach (Control control in new Control[] {heading,subtitle,remote,reconnect,refresh,operation,progress,footer,version,detailsToggle,details}) viewport.Controls.Add(control);
            remote.Name="remoteCard"; reconnect.Name="reconnectCard"; remote.BackColor=reconnect.BackColor=Color.White;
            foreach (Control control in new Control[] {remoteHeading,remoteStatus,remoteDetail,deploy,uninstall,remoteHint,proxyInfo,proxySettings,proxyCaption,proxy,proxyHint}) remote.Controls.Add(control);
            foreach (Control control in new Control[] {retryHeading,retryStatus,retryDetail,repair,restore,retryHint}) reconnect.Controls.Add(control);
            StyleLabel(heading,"heading","Codex 连接修复",20,true,Ink);
            StyleLabel(subtitle,"subtitle","按问题选择操作，当前状态会自动检测。",9.5f,false,Muted);
            StyleLabel(remoteHeading,"remoteHeading","解决无法添加远程控制设备问题",11.5f,true,Ink);
            StyleLabel(retryHeading,"retryHeading","解决每次使用codex重新连接5次问题",11.5f,true,Ink);
            StyleLabel(remoteStatus,"remoteStatus","正在检查转发器…",10.5f,true,Muted);
            StyleLabel(retryStatus,"retryStatus","正在检查重连设置…",10.5f,true,Muted);
            StyleLabel(remoteDetail,"remoteDetail","检查现有部署、后台进程与连接通路。",9.5f,false,Muted);
            StyleLabel(retryDetail,"retryDetail","读取代理开关并检查 HTTP / WebSocket 通道。",9.5f,false,Muted);
            StyleLabel(remoteHint,"remoteHint","关闭工具不会停止后台；代理软件需保持运行。",9,false,Muted);
            StyleLabel(retryHint,"retryHint","修改前备份，还原只恢复代理开关，保留其他设置。",9,false,Muted);
            StyleLabel(proxyInfo,"proxyInfo","代理：自动检测",9,false,Muted);
            StyleLabel(proxyCaption,"proxyCaption","HTTP / 混合端口",9,false,Ink);
            StyleLabel(proxyHint,"proxyHint","留空自动检测，例如 7890。SOCKS 专用端口不适用。",9,false,Muted);
            StyleLabel(operation,"operation","",9.5f,false,Ink);
            StyleLabel(footer,"footer","等待检测",9,false,Muted);
            StyleLabel(version,"version","Windows 10/11 · v1.0.2",9,false,Muted);
            StyleButton(deploy,"deploy","一键部署转发器",true); StyleButton(uninstall,"uninstall","一键卸载转发器",false);
            StyleButton(repair,"repair","一键修复重连",true); StyleButton(restore,"restore","还原重连修复前配置",false);
            StyleButton(refresh,"refresh","刷新检测",false);
            StyleLink(proxySettings,"proxySettings","代理设置 ▸"); StyleLink(detailsToggle,"detailsToggle","诊断详情 ▸");
            proxy.Name="proxyPort"; proxy.Font=new Font("Microsoft YaHei UI",10); proxy.BorderStyle=BorderStyle.FixedSingle; proxy.AccessibleName="手动 HTTP 代理端口，留空自动检测";
            details.Name="diagnosticDetails"; details.Multiline=true; details.ReadOnly=true; details.TabStop=false; details.ScrollBars=ScrollBars.Vertical;
            details.Font=new Font("Microsoft YaHei UI",9); details.BorderStyle=BorderStyle.FixedSingle; details.BackColor=Color.White;
            progress.Style=ProgressBarStyle.Marquee;
            proxySettings.LinkClicked += delegate { proxyExpanded=!proxyExpanded; Relayout(); };
            detailsToggle.LinkClicked += delegate { detailsExpanded=!detailsExpanded; Relayout(); };
            refresh.Click += async delegate { await RunOperation(DetectAll); };
            deploy.Click += async delegate { await RunOperation(Deploy); };
            uninstall.Click += async delegate { await RunOperation(Uninstall); };
            repair.Click += async delegate { await RunOperation(delegate { return ChangeReconnect(false); }); };
            restore.Click += async delegate { await RunOperation(delegate { return ChangeReconnect(true); }); };
            viewport.SizeChanged += delegate { Relayout(); };
            Shown += async delegate { Relayout(); if (this.autoDetect) await RunOperation(DetectAll); };
            FormClosing += delegate(object sender,FormClosingEventArgs e) { if (busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel=true; footer.Text="请等待当前检查或操作完成"; } };
            UpdateButtons(); Relayout();
        }
        private int PutText(Label label,int x,int y,int width,int gap) {
            int height=TextRenderer.MeasureText(label.Text,label.Font,new Size(Math.Max(1,width),Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix).Height+Px(3);
            label.SetBounds(x,y,width,Math.Max(height,label.Font.Height)); return y+label.Height+Px(gap);
        }
        private int PutButtons(Button first,Button second,int x,int y,int width) {
            int preferred=Px(192), gap=Px(12), height=Px(40);
            if (width >= preferred*2+gap) {
                first.SetBounds(x,y,preferred,height); second.SetBounds(x+preferred+gap,y,preferred,height); return y+height+Px(12);
            }
            int buttonWidth=(width-gap)/2;
            first.SetBounds(x,y,buttonWidth,height); second.SetBounds(x+buttonWidth+gap,y,buttonWidth,height); return y+height+Px(12);
        }
        private void Relayout() {
            if (layingOut || IsDisposed || viewport.ClientSize.Width < 1) return;
            layingOut=true;
            try {
                viewport.SuspendLayout();
                int width=Math.Max(Px(430),Math.Min(Px(820),viewport.ClientSize.Width-Px(40)-SystemInformation.VerticalScrollBarWidth));
                int left=Math.Max(Px(16),(viewport.ClientSize.Width-width)/2), scroll=viewport.AutoScrollPosition.Y, y=Px(20)+scroll;
                int headerWidth=width-Px(125);
                int headerEnd=PutText(heading,left,y,headerWidth,2);
                refresh.SetBounds(left+width-Px(112),y+Px(5),Px(112),Px(36));
                headerEnd=Math.Max(headerEnd,refresh.Bottom+Px(5));
                y=PutText(subtitle,left,headerEnd,width,16);
                int inset=Px(18), inner=width-inset*2, row=Px(16);
                row=PutText(remoteHeading,inset,row,inner,12);
                row=PutText(remoteStatus,inset,row,inner,4);
                row=PutText(remoteDetail,inset,row,inner,14);
                row=PutButtons(deploy,uninstall,inset,row,inner);
                row=PutText(remoteHint,inset,row,inner,9);
                int proxyLine=PutText(proxyInfo,inset,row,inner-Px(104),0);
                proxySettings.Text=proxyExpanded ? "代理设置 ▾" : "代理设置 ▸";
                proxySettings.SetBounds(width-inset-Px(92),row,Px(92),Math.Max(Px(24),proxySettings.Font.Height+Px(4)));
                row=Math.Max(proxyLine,row+proxySettings.Height);
                proxy.Visible=proxyCaption.Visible=proxyHint.Visible=proxyExpanded;
                if (proxyExpanded) {
                    row+=Px(10); row=PutText(proxyCaption,inset,row,inner,5);
                    proxy.SetBounds(inset,row,Px(184),proxy.PreferredHeight); row+=proxy.Height+Px(7);
                    row=PutText(proxyHint,inset,row,inner,0);
                }
                remote.SetBounds(left,y,width,row+Px(12)); y+=remote.Height+Px(14);
                row=Px(16); row=PutText(retryHeading,inset,row,inner,12); row=PutText(retryStatus,inset,row,inner,4);
                row=PutText(retryDetail,inset,row,inner,14); row=PutButtons(repair,restore,inset,row,inner); row=PutText(retryHint,inset,row,inner,0);
                reconnect.SetBounds(left,y,width,row+Px(14)); y+=reconnect.Height+Px(12);
                operation.Visible=!String.IsNullOrWhiteSpace(operation.Text);
                if (operation.Visible) y=PutText(operation,left,y,width,10);
                progress.Visible=busy; progress.SetBounds(left,y,width,Px(3)); if (busy) y+=Px(10);
                int footerWidth=width-Px(185); int footerEnd=PutText(footer,left,y,footerWidth,6);
                PutText(version,left+width-Px(185),y,Px(185),0); y=footerEnd;
                detailsToggle.Text=detailsExpanded ? "诊断详情 ▾" : "诊断详情 ▸";
                detailsToggle.SetBounds(left,y,Px(130),Math.Max(Px(26),detailsToggle.Font.Height+Px(4))); y+=detailsToggle.Height;
                details.Visible=detailsExpanded;
                if (detailsExpanded) { details.SetBounds(left,y,width,Px(170)); y+=details.Height+Px(6); }
                viewport.AutoScrollMinSize=new Size(0,y-scroll+Px(18));
            } finally { viewport.ResumeLayout(true); layingOut=false; }
        }
        private void UpdateButtons() {
            refresh.Enabled=!busy; proxy.Enabled=!busy;
            deploy.Enabled=!busy && remoteView != null && remoteView.CanDeploy;
            uninstall.Enabled=!busy && remoteView != null && remoteView.CanUninstall;
            repair.Enabled=!busy && retryView != null && retryView.CanRepair;
            restore.Enabled=!busy && retryView != null && retryView.CanRestore;
            foreach (Button button in new[] {deploy,uninstall,repair,restore}) {
                bool primary=button == deploy || button == repair;
                button.BackColor=button.Enabled ? (primary ? Blue : Color.White) : Color.FromArgb(236,241,247);
                button.ForeColor=button.Enabled && primary ? Color.White : Muted;
                button.FlatAppearance.BorderColor=button.Enabled && primary ? Blue : Color.FromArgb(213,222,234);
            }
        }
        private void UpdateDetails() { details.Text="转发器\r\n"+relayReport+"\r\n\r\n重连设置\r\n"+retryReport+"\r\n\r\n代理\r\n"+proxyReport; }
        private async Task RunOperation(Func<Task<string>> action) {
            if (busy) return; busy=true; operation.Text=""; footer.Text="正在检测或处理，请稍候…"; UpdateButtons(); Relayout();
            try { string result=await action(); operation.ForeColor=Ink; operation.Text=result ?? ""; footer.Text="已更新 · "+DateTime.Now.ToString("HH:mm:ss"); }
            catch (System.ComponentModel.Win32Exception e) { operation.ForeColor=Amber; operation.Text=e.NativeErrorCode == 1223 ? "已取消 Windows 管理员确认。" : e.Message; footer.Text="操作未完成"; }
            catch (Exception e) { operation.ForeColor=Amber; operation.Text=e.Message; footer.Text="操作未完成，请查看提示"; }
            finally { busy=false; UpdateButtons(); UpdateDetails(); Relayout(); }
        }
        private async Task DetectRelay() {
            var evidence=await Task.Run(delegate { return DiagnosticInspector.Relay(); });
            remoteView=StatusRules.Relay(evidence); remoteStatus.Text=remoteView.Title; remoteStatus.ForeColor=remoteView.Good ? Green : Amber; remoteDetail.Text=remoteView.Detail;
            relayReport=evidence.Details ?? evidence.Error ?? remoteView.Detail; UpdateDetails(); Relayout();
        }
        private async Task DetectReconnect() {
            var evidence=await Task.Run(delegate { return DiagnosticInspector.Reconnect(); });
            retryView=StatusRules.Reconnect(evidence); retryStatus.Text=retryView.Title; retryStatus.ForeColor=retryView.Good ? Green : Amber; retryDetail.Text=retryView.Detail;
            retryReport=(evidence.Details ?? "")+(String.IsNullOrEmpty(evidence.Error) ? "" : "\r\n"+evidence.Error); UpdateDetails(); Relayout();
        }
        private async Task DetectProxy() {
            try { var endpoint=await ResolveProxy(); proxyInfo.Text="代理："+endpoint+" · 可用"; proxyReport="已通过加密通路检测："+endpoint; }
            catch(Exception e) { proxyInfo.Text="代理：未检测到可用通路"; proxyReport=e.Message; }
            UpdateDetails(); Relayout();
        }
        private async Task<string> DetectAll() { await Task.WhenAll(DetectRelay(),DetectReconnect(),DetectProxy()); return ""; }
        private async Task<ProxyEndpoint> ResolveProxy() {
            string text=proxy.Text.Trim();
            if (text.Length > 0) {
                ProxyEndpoint endpoint;
                if (!ProxyEndpoint.TryParse(text,out endpoint)) throw new InvalidOperationException("请输入本机 HTTP / 混合端口，例如 7890 或 127.0.0.1:15236。");
                await Tunnel.Probe(endpoint); return endpoint;
            }
            foreach (var endpoint in ProxyDiscovery.Candidates()) { try { await Tunnel.Probe(endpoint); return endpoint; } catch { } }
            throw new InvalidOperationException("请开启代理软件和系统代理，或在“代理设置”中填写 HTTP / 混合端口。");
        }
        private static async Task<bool> Elevated(string args) {
            return await Task.Run(delegate {
                var info=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,args) { UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden };
                using(var child=Process.Start(info)) { child.WaitForExit(); return child.ExitCode == 0; }
            });
        }
        private async Task<string> Deploy() {
            if (!DesktopIdentity.IsCurrentDesktopUser()) throw new InvalidOperationException("请用桌面登录用户打开工具后部署；需要管理员权限时会单独请求确认。");
            if (MessageBox.Show(this,"将设置登录启动和后台恢复，让本机所有应用对 chatgpt.com 的直连通过现有代理。代理关闭期间连接不可用。\r\n\r\n可通过卸载撤销。继续部署？","部署转发器",MessageBoxButtons.OKCancel,MessageBoxIcon.Information) != DialogResult.OK) return "已取消部署。";
            var endpoint=await ResolveProxy(); bool success=await Elevated("--apply-install "+endpoint+" "+WindowsIdentity.GetCurrent().User.Value);
            await DetectAll(); return success ? "转发器已部署。请到 Codex 连接设置中完成设备配对。" : "部署未完成，请参考错误提示。";
        }
        private async Task<string> Uninstall() {
            if (MessageBox.Show(this,"将停止并删除已识别的转发后台任务，撤销对应 hosts 映射。程序及备份保留。\r\n\r\n继续卸载？","卸载转发器",MessageBoxButtons.OKCancel,MessageBoxIcon.Question) != DialogResult.OK) return "已取消卸载。";
            bool success=await Elevated("--apply-uninstall"); await DetectAll(); return success ? "转发任务和映射已撤销；程序及备份已保留。" : "卸载未完成，请参考错误提示。";
        }
        private async Task<string> ChangeReconnect(bool undo) {
            if (!DesktopIdentity.IsCurrentDesktopUser()) throw new InvalidOperationException("当前运行账户与桌面登录用户不同，已停止配置修改。请用桌面用户打开工具。");
            if (!undo) {
                using (var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                    if (key == null || Convert.ToInt32(key.GetValue("ProxyEnable",0)) != 1) throw new InvalidOperationException("请先在代理软件中开启“系统代理”。");
            }
            return await DiagnosticInspector.MutateAndRefresh(delegate {
                var cli=new CodexCli(CodexCli.Find()); var fix=new ReconnectFix(ReconnectFix.ConfigFile(),ReconnectFix.BackupDirectory(),cli);
                return undo ? fix.Restore() : fix.Repair();
            },DetectReconnect);
        }
    }
}
