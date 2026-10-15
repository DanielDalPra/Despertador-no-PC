using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Linq;

public class AlarmItem {
    public DateTime At;
    public string State = "Agendado";
    public bool Test;
}

public class AlarmWindow : Form {
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] struct PointNative { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out PointNative p);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PointNative p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    readonly List<AlarmItem> alarms = new List<AlarmItem>();
    readonly string folder;
    readonly string settings;
    readonly Timer timer = new Timer { Interval = 200 };
    readonly Label clock = new Label(), status = new Label(), next = new Label(), notice = new Label(), clickStatus = new Label();
    readonly ListView list = new ListView();
    readonly DateTimePicker date = new DateTimePicker(), time = new DateTimePicker();
    readonly TextBox answer = new TextBox();
    readonly Button remove = new Button();
    readonly SoundPlayer sound;
    readonly MemoryStream wave;
    AlarmItem active;
    DateTime stopAt, captureAt;
    bool capturing, clickReady, initialized;
    PointNative clickPoint;
    IntPtr clickWindow;
    string fault = "";
    readonly bool silentVerification;

    public static List<AlarmItem> Defaults() {
        return new [] { 6.0, 6.5, 7.0 }.Select(h => new AlarmItem { At = new DateTime(2026,10,14).AddHours(h) }).ToList();
    }
    public static bool IsDue(AlarmItem a, DateTime now) { return a.State == "Agendado" && a.At <= now; }
    public static byte[] MakeWave() {
        using (var stream = new MemoryStream()) using (var w = new BinaryWriter(stream)) {
            int rate = 22050, count = rate * 2;
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + count * 2);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
            w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(count * 2);
            double phase = 0, bass = 0;
            for (int i=0; i<count; i++) {
                double t = i / (double)rate;
                phase = (phase + (t % 0.5 < 0.25 ? 880 : 1320) / (double)rate) % 1;
                bass = (bass + 110.0 / rate) % 1;
                w.Write((short)Math.Round(32767 * (0.7 * (phase < .5 ? 1 : -1) + .3 * (bass < .5 ? 1 : -1))));
            }
            w.Flush(); return stream.ToArray();
        }
    }
    static void MaxVolume() {
        for (int i=0;i<100;i++) { keybd_event(0xAF,0,0,UIntPtr.Zero); keybd_event(0xAF,0,2,UIntPtr.Zero); }
    }
    void Log(string message) {
        try { File.AppendAllText(Path.Combine(folder,"historico-alarmes.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine); }
        catch { /* Logging must never prevent the alarm from sounding. */ }
    }
    void Save() {
        try {
            var doc = new XDocument(new XElement("alarmes", alarms.Where(a=>!a.Test).Select(a =>
                new XElement("alarme", new XAttribute("data",a.At.ToString("o")), new XAttribute("estado",a.State)))));
            string temp = settings + ".tmp";
            doc.Save(temp);
            if (File.Exists(settings)) File.Replace(temp, settings, settings + ".bak"); else File.Move(temp,settings);
        } catch (Exception e) { fault = "Nao foi possivel salvar. Mantenha esta janela aberta. " + e.Message; Log(fault); }
    }
    void LoadSchedule() {
        if (!File.Exists(settings)) { alarms.AddRange(Defaults()); Save(); return; }
        try {
            var loaded = XDocument.Load(settings).Root.Elements("alarme").Select(x=>new AlarmItem {
                At = DateTime.ParseExact((string)x.Attribute("data"),"o",System.Globalization.CultureInfo.InvariantCulture),
                State = (string)x.Attribute("estado") ?? "Agendado"
            }).ToList();
            foreach (var a in loaded) if (a.State == "Tocando") a.State = "Agendado";
            alarms.AddRange(loaded);
        } catch (Exception e) {
            try { File.Copy(settings, settings + ".corrompido-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch { }
            alarms.AddRange(Defaults());
            fault = "Falha ao ler configuracao; restaurados 14/10/2026, 06:00, 06:30 e 07:00. Confira a lista.";
            Log(e.ToString());
        }
        foreach (var a in alarms.Where(a=>a.State == "Agendado" && a.At < DateTime.Now.AddMinutes(-5))) a.State = "Horario passou";
        Save();
    }
    Label LabelAt(string text, int x, int y, int width, int height, float size) {
        var l = new Label { Text=text, Location=new Point(x,y), Size=new Size(width,height), Font=new Font("Segoe UI",size), ForeColor=Color.FromArgb(226,232,240) };
        Controls.Add(l); return l;
    }
    Button ButtonAt(string text,int x,int y,int width, EventHandler action) {
        var b = new Button { Text=text, Location=new Point(x,y), Size=new Size(width,36), FlatStyle=FlatStyle.Flat, BackColor=Color.FromArgb(42,55,76), ForeColor=Color.White, Font=new Font("Segoe UI",10) };
        b.Click += action; Controls.Add(b); return b;
    }
    public AlarmWindow(string outputFolder) : this(outputFolder, false) {}
    AlarmWindow(string outputFolder, bool silent) {
        silentVerification=silent;
        folder=outputFolder; settings=Path.Combine(folder,"alarmes-config.xml");
        Text="Despertador | alarmes ativos"; ClientSize=new Size(830,720); MinimumSize=Size; MaximumSize=Size;
        StartPosition=FormStartPosition.CenterScreen; BackColor=Color.FromArgb(15,23,42); Font=new Font("Segoe UI",10);
        AutoScaleMode=AutoScaleMode.None;
        LabelAt("DESPERTADOR",24,18,500,35,21);
        clock=LabelAt("",24,61,780,27,13);
        status=LabelAt("",24,100,780,31,17);
        next=LabelAt("",24,137,780,30,14);
        LabelAt("Data e hora pelo relogio do Windows. Abrir esta janela ja ativa os horarios agendados.",24,174,780,24,10);
        list.Location=new Point(24,207); list.Size=new Size(780,172); list.View=View.Details;
        list.FullRowSelect=true; list.MultiSelect=false; list.HideSelection=false;
        list.Columns.Add("Data",180); list.Columns.Add("Horario",150); list.Columns.Add("Estado",410);
        list.BackColor=Color.FromArgb(30,41,59); list.ForeColor=Color.White; Controls.Add(list);
        date.Location=new Point(24,397); date.Size=new Size(160,28); date.Format=DateTimePickerFormat.Custom; date.CustomFormat="dd/MM/yyyy";
        date.Value=new DateTime(2026,10,14); Controls.Add(date);
        time.Location=new Point(195,397); time.Size=new Size(100,28); time.Format=DateTimePickerFormat.Custom;
        time.CustomFormat="HH:mm"; time.ShowUpDown=true; time.Value=DateTime.Today.AddHours(6); Controls.Add(time);
        ButtonAt("Adicionar horario",308,392,168,(s,e)=>AddAlarm());
        remove=ButtonAt("Remover selecionado",489,392,205,(s,e)=>RemoveAlarm());
        ButtonAt("Teste em 1 minuto",24,442,200,(s,e)=>TestAlarm());
        ButtonAt("Configurar clique JBL",238,442,220,(s,e)=>CaptureClick());
        ButtonAt("Desativar clique JBL",472,442,220,(s,e)=>{clickReady=false; capturing=false; clickStatus.Text="Clique JBL desligado. O som continua ativo.";});
        clickStatus=LabelAt("Clique JBL opcional: configure nesta abertura; deixe o app visivel e desbloqueado.",24,487,780,35,10);
        LabelAt("Quando tocar, digite ACORDEI e pressione Enter:",24,531,780,25,11);
        answer.Location=new Point(24,562); answer.Size=new Size(435,30); answer.CharacterCasing=CharacterCasing.Upper;
        answer.Enabled=false; answer.KeyDown+=(s,e)=> { if(e.KeyCode==Keys.Enter) { e.SuppressKeyPress=true; Acknowledge(); } }; Controls.Add(answer);
        ButtonAt("Confirmar ACORDEI",472,559,220,(s,e)=>Acknowledge());
        notice=LabelAt("",24,608,780,88,10);
        wave=new MemoryStream(MakeWave()); sound=new SoundPlayer(wave); sound.Load();
        LoadSchedule(); RefreshList();
        timer.Tick+=(s,e)=>TickAlarm();
        Shown+=(s,e)=> {
            uint result=SetThreadExecutionState(0x80000001);
            if(result==0) fault="Nao foi possivel impedir suspensao. Ajuste as opcoes de energia do Windows.";
            initialized=true; timer.Start(); TickAlarm(); Log("Interface aberta; agenda ativa.");
        };
        FormClosing+=(s,e)=> {
            if (initialized && MessageBox.Show(this,"Fechar desliga TODOS os alarmes desta janela. Quer mesmo encerrar?","Encerrar despertador?",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes) {e.Cancel=true; return;}
            timer.Stop(); sound.Stop();
            if(active!=null) active.State="Interrompido ao fechar";
            Save(); SetThreadExecutionState(0x80000000); sound.Dispose(); wave.Dispose(); Log("Interface encerrada.");
        };
    }
    void RefreshList() {
        list.Items.Clear();
        foreach(var a in alarms.OrderBy(a=>a.At)) {
            var row=new ListViewItem(a.At.ToString("dd/MM/yyyy")); row.SubItems.Add(a.At.ToString("HH:mm:ss"));
            row.SubItems.Add(a.State+(a.Test ? " (teste)":"")); row.Tag=a; list.Items.Add(row);
        }
    }
    void AddAlarm() {
        DateTime at=date.Value.Date.AddHours(time.Value.Hour).AddMinutes(time.Value.Minute);
        if(at<=DateTime.Now) { MessageBox.Show(this,"Esse horario ja passou. Escolha uma data e hora futuras."); return; }
        if(alarms.Any(a=>a.At==at && a.State=="Agendado")) {MessageBox.Show(this,"Esse horario ja esta ativo.");return;}
        alarms.Add(new AlarmItem { At=at }); Save(); RefreshList(); TickAlarm();
    }
    void RemoveAlarm() {
        if(list.SelectedItems.Count==0)return;
        var a=(AlarmItem)list.SelectedItems[0].Tag;
        if(a==active) { MessageBox.Show(this,"Para parar o toque, digite ACORDEI."); return; }
        if(MessageBox.Show(this,"Remover o alarme de "+a.At.ToString("dd/MM/yyyy HH:mm")+"?","Remover horario",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
        alarms.Remove(a); Save(); RefreshList(); TickAlarm();
    }
    void TestAlarm() {
        var a=new AlarmItem { At=DateTime.Now.AddMinutes(1), Test=true };
        alarms.Add(a); RefreshList(); Log("Teste agendado para "+a.At.ToString("HH:mm:ss"));
        notice.Text="TESTE: vai tocar em um minuto, no volume maximo. Tire o fone dos ouvidos.";
    }
    void CaptureClick() {
        capturing=true; clickReady=false; captureAt=DateTime.Now.AddSeconds(10);
        clickStatus.Text="Em 10 segundos: coloque o mouse sobre Falha no app JBL, sem clicar.";
    }
    void Acknowledge() {
        if(active==null)return;
        if(!String.Equals(answer.Text.Trim(),"ACORDEI",StringComparison.OrdinalIgnoreCase)) {answer.SelectAll();answer.Focus();return;}
        StopAlarm("Confirmado: ACORDEI");
    }
    void StopAlarm(string state) {
        sound.Stop(); Log(active.At.ToString("dd/MM/yyyy HH:mm:ss")+" - "+state);
        active.State=state; active=null; answer.Clear(); answer.Enabled=false; Save(); RefreshList();
    }
    void StartAlarm(AlarmItem a) {
        active=a; a.State="Tocando"; stopAt=DateTime.Now.AddMinutes(5);
        answer.Clear(); answer.Enabled=true;
        // Audio is started before optional UI integration; a failed click cannot block sound.
        try {if(!silentVerification) {MaxVolume(); sound.PlayLooping();}} catch(Exception e) {fault="ERRO NO SOM: "+e.Message; Log(fault);}
        Save(); RefreshList(); Log("DISPAROU: "+a.At.ToString("dd/MM/yyyy HH:mm:ss"));
        if(clickReady) {
            clickReady=false;
            try {
                if(WindowFromPoint(clickPoint)==clickWindow && clickWindow!=IntPtr.Zero && SetCursorPos(clickPoint.X,clickPoint.Y)) {
                    mouse_event(2,0,0,0,UIntPtr.Zero);mouse_event(4,0,0,0,UIntPtr.Zero);
                    clickStatus.Text="Clique JBL enviado. Reconfigure se quiser um novo clique.";
                } else clickStatus.Text="Clique JBL ignorado: alvo mudou ou esta coberto. Som continua ativo.";
            } catch {clickStatus.Text="Clique JBL falhou. Som continua ativo.";}
        }
        if(!silentVerification) {Show(); WindowState=FormWindowState.Normal; Activate(); answer.Focus();}
    }
    void TickAlarm() {
        var now=DateTime.Now;
        clock.Text="Agora: "+now.ToString("dddd, dd/MM/yyyy HH:mm:ss");
        if(capturing) {
            if(now>=captureAt) {
                capturing=false; clickReady=GetCursorPos(out clickPoint); clickWindow=clickReady ? WindowFromPoint(clickPoint):IntPtr.Zero;
                clickReady=clickWindow!=IntPtr.Zero;
                clickStatus.Text=clickReady ? "Posicao salva. Nao mova nem cubra o app JBL. Clique no proximo alarme (inclusive teste)." : "Falha ao salvar ponto. Som continua ativo.";
            } else clickStatus.Text="Posicione o mouse sobre Falha: captura em "+Math.Ceiling((captureAt-now).TotalSeconds)+" s.";
        }
        if(active!=null && now>=stopAt)StopAlarm("Concluido: limite de 5 minutos");
        if(active==null) {
            var due=alarms.Where(a=>IsDue(a,now)).OrderBy(a=>a.At).FirstOrDefault();
            if(due!=null)StartAlarm(due);
        }
        var pending=alarms.Where(a=>a.State=="Agendado").OrderBy(a=>a.At).FirstOrDefault();
        status.Text=active!=null ? "TOCANDO - digite ACORDEI" : pending!=null ? "ATIVO - aguardando horario" : "SEM ALARMES FUTUROS - adicione um horario";
        status.ForeColor=active!=null ? Color.Orange : pending!=null ? Color.LightGreen:Color.Orange;
        next.Text=active!=null ? "Parada automatica: "+stopAt.ToString("HH:mm:ss") : pending!=null ? "Proximo: "+pending.At.ToString("dd/MM/yyyy HH:mm:ss")+"  |  faltam "+FormatRemaining(pending.At-now) : "Confira a data antes de dormir.";
        Text=active!=null ? "ALARME TOCANDO - ACORDEI" : pending!=null ? "ATIVO | "+pending.At.ToString("dd/MM HH:mm")+" | Despertador" : "Sem alarmes futuros | Despertador";
        if(fault.Length>0) {notice.Text=fault;notice.ForeColor=Color.Orange;}
        else {
            notice.Text="Mantenha esta janela aberta, PC na tomada e tampa aberta. Suspensao automatica bloqueada enquanto aberto.\nFone ligado, carregado e selecionado como saida. Volume maximo no disparo: teste fora dos ouvidos.\nMinimizar e permitido. Fechar, desligar, hibernar ou reiniciar interrompe os alarmes.";
            notice.ForeColor=Color.FromArgb(203,213,225);
        }
    }
    static string FormatRemaining(TimeSpan t) {return t.TotalSeconds<0 ? "agora" : ((int)t.TotalHours).ToString("00")+":"+t.Minutes.ToString("00")+":"+t.Seconds.ToString("00");}
    public static void VerifyLifecycle(string directory) {
        Directory.CreateDirectory(directory);
        var window=new AlarmWindow(directory,true);
        // Keep fixed real alarms in the future for this isolated verification.
        window.alarms.Clear(); window.alarms.AddRange(Defaults());
        var test=new AlarmItem {At=DateTime.Now.AddSeconds(-1),Test=true};
        window.alarms.Add(test); window.TickAlarm();
        if(window.active!=test || !window.answer.Enabled)throw new Exception("Test did not trigger");
        window.answer.Text="ERRADO"; window.Acknowledge();
        if(window.active!=test)throw new Exception("Incorrect answer stopped alarm");
        window.answer.Text="acordei"; window.Acknowledge();
        if(window.active!=null || test.State!="Confirmado: ACORDEI")throw new Exception("Correct answer did not stop alarm");
        var timeout=new AlarmItem {At=DateTime.Now.AddSeconds(-1),Test=true};
        window.alarms.Add(timeout); window.TickAlarm(); window.stopAt=DateTime.Now.AddSeconds(-1); window.TickAlarm();
        if(window.active!=null || timeout.State!="Concluido: limite de 5 minutos")throw new Exception("Automatic stop failed");
        if(window.alarms.Count(a=>!a.Test && a.State=="Agendado")!=3)throw new Exception("Real schedule changed");
        var saved=XDocument.Load(window.settings).Root.Elements("alarme").ToArray();
        if(saved.Length!=3 || saved.Any(x=>(string)x.Attribute("estado")!="Agendado"))throw new Exception("Persistence failed");
        window.timer.Dispose(); window.sound.Dispose();window.wave.Dispose();window.Dispose();
    }
}

public static class AlarmApp {
    [STAThread] public static void Main(string[] args) {
        if(args.Contains("--self-test")) {
            var a=AlarmWindow.Defaults();
            if(a.Count!=3 || a[0].At!=new DateTime(2026,10,14,6,0,0) || a[1].At!=new DateTime(2026,10,14,6,30,0) || a[2].At!=new DateTime(2026,10,14,7,0,0))throw new Exception("Default schedule");
            if(AlarmWindow.IsDue(a[0],a[0].At.AddSeconds(-1)) || !AlarmWindow.IsDue(a[0],a[0].At))throw new Exception("Due boundary");
            a[0].State="Confirmado"; if(AlarmWindow.IsDue(a[0],a[0].At.AddHours(1)))throw new Exception("Repeated alarm");
            byte[] wav=AlarmWindow.MakeWave(); int peak=0;
            if(wav.Length!=88244)throw new Exception("WAV length");
            for(int i=44;i<wav.Length;i+=2)peak=Math.Max(peak,Math.Abs((int)BitConverter.ToInt16(wav,i)));
            if(peak!=32767)throw new Exception("Audio peak");
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"validacao-interface.txt"),"PASS: 14/10/2026 06:00, 06:30, 07:00; limites de disparo; sem repeticao; WAV 32767/32767. Nenhum som reproduzido.");
            return;
        }
        if(args.Length==2 && args[0]=="--verify-lifecycle") {
            AlarmWindow.VerifyLifecycle(args[1]);
            File.WriteAllText(Path.Combine(args[1],"resultado.txt"),"PASS: disparo por horario; resposta incorreta nao para; ACORDEI para; limite de cinco minutos simulado; proximos alarmes preservados; agenda salva. Sem som e sem cliques.");
            return;
        }
        bool created;
        using(var mutex=new System.Threading.Mutex(true,"Local\\DespertadorCodexInterface",out created)) {
            if(!created) {MessageBox.Show("A interface ja esta aberta. Procure Despertador na barra de tarefas."); return;}
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new AlarmWindow(AppDomain.CurrentDomain.BaseDirectory));
        }
    }
}
