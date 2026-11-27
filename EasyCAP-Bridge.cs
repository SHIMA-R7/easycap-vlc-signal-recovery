using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Collections.Generic;
public sealed class BridgeConfig {
 public string FFmpeg {get;set;} public string VLC {get;set;}
 public string[] FFmpegArguments {get;set;} public string[] VLCArguments {get;set;}
 public string FFmpegLog {get;set;} public string RecordingPath {get;set;}
 public string StopFile {get;set;} public string StatusFile {get;set;}
}
internal static class EasyCAPBridge {
 static bool StopRequested(BridgeConfig c) {
  return !string.IsNullOrEmpty(c.StopFile)&&File.Exists(c.StopFile);
 }
 static void Report(BridgeConfig c,string status) {
  if(string.IsNullOrEmpty(c.StatusFile))return;
  try{File.WriteAllText(c.StatusFile,status,new UTF8Encoding(false));}catch(IOException){}catch(UnauthorizedAccessException){}
 }
 static void ReportDetail(BridgeConfig c,string suffix,string text) {
  if(string.IsNullOrEmpty(c.StatusFile))return;
  try{File.WriteAllText(c.StatusFile+suffix,text,new UTF8Encoding(false));}catch(IOException){}catch(UnauthorizedAccessException){}
 }
 // Windows CommandLineToArgvW-compatible quoting, including embedded quotes.
 static string Quote(string s) {
  var b=new StringBuilder("\""); int slashes=0;
  foreach(char c in s) { if(c=='\\'){slashes++;continue;} if(c=='\"'){b.Append('\\',slashes*2+1);b.Append(c);}else{b.Append('\\',slashes);b.Append(c);}slashes=0; }
  b.Append('\\',slashes*2);b.Append('"');return b.ToString();
 }
 static ProcessStartInfo Info(string exe,string[] args) {
  return new ProcessStartInfo(exe,string.Join(" ",args.Select(Quote).ToArray())){UseShellExecute=false,CreateNoWindow=true};
 }
 static void StopFFmpeg(Process p) {
  if(p==null||p.HasExited)return;
  try{p.StandardInput.WriteLine("q");p.StandardInput.Flush();}catch{}
  if(!p.WaitForExit(5000)){p.Kill();p.WaitForExit();}
 }
 static void FinalizeRecording(BridgeConfig c,StreamWriter log) {
  if(string.IsNullOrEmpty(c.RecordingPath)||!File.Exists(c.RecordingPath))return;
  string temp=c.RecordingPath+".finalizing-"+Guid.NewGuid().ToString("N")+".mkv";
  try{
   var info=Info(c.FFmpeg,new[]{"-hide_banner","-nostdin","-v","error","-n","-i",c.RecordingPath,"-map","0","-c","copy",temp});
   info.RedirectStandardError=true;
   using(var p=Process.Start(info)){string error=p.StandardError.ReadToEnd();p.WaitForExit();if(p.ExitCode!=0)throw new IOException(error);}
   // The destination was created exclusively by this capture; replace only after a complete remux.
   File.Replace(temp,c.RecordingPath,null);
   log.WriteLine("Recording finalized with duration and seek index: "+c.RecordingPath);
  }catch(Exception e){log.WriteLine("Recording kept in playable live Matroska form; index finalization failed: "+e.Message);}
 }
 static int Main(string[] args) {
  Process ff=null,vlc=null;FileStream recording=null;StreamWriter log=null;Mutex deviceLock=null;bool ownsDevice=false;BridgeConfig c=null;
  try {
   if(args.Length!=1)throw new ArgumentException("Expected bridge configuration path.");
   c=new JavaScriptSerializer().Deserialize<BridgeConfig>(File.ReadAllText(args[0],Encoding.UTF8));
   deviceLock=new Mutex(false,@"Local\EasyCAP-Capture-Device");
   try{ownsDevice=deviceLock.WaitOne(0);}catch(AbandonedMutexException){ownsDevice=true;}
   if(!ownsDevice)throw new Exception("EasyCAP is already in use. Close the other EasyCAP capture first.");
   Report(c,"starting");
   log=new StreamWriter(c.FFmpegLog,false,new UTF8Encoding(false));log.AutoFlush=true;
   if(!string.IsNullOrEmpty(c.RecordingPath))recording=new FileStream(c.RecordingPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
   var vi=Info(c.VLC,c.VLCArguments);vi.RedirectStandardInput=true;
   vlc=Process.Start(vi);
   var fi=Info(c.FFmpeg,c.FFmpegArguments);fi.RedirectStandardInput=true;fi.RedirectStandardOutput=true;fi.RedirectStandardError=true;fi.StandardErrorEncoding=Encoding.UTF8;
   ff=Process.Start(fi);
   var logger=log;long frames=0,bytes=0,lastFrameTick=Environment.TickCount;var diagnostic=new StringBuilder();var diagnosticLock=new object();
   Task errors=Task.Run(()=>{string line;while((line=ff.StandardError.ReadLine())!=null){
    logger.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ")+line);
    long count;if(line.StartsWith("frame=")&&long.TryParse(line.Substring(6).Trim(),out count)){
     long previous=Interlocked.Exchange(ref frames,count);if(count>previous)Interlocked.Exchange(ref lastFrameTick,Environment.TickCount);
    }
    if(line.IndexOf("error",StringComparison.OrdinalIgnoreCase)>=0||line.IndexOf("Could not",StringComparison.OrdinalIgnoreCase)>=0){lock(diagnosticLock){diagnostic.AppendLine(line);if(diagnostic.Length>1800)diagnostic.Remove(0,diagnostic.Length-1800);}}
   }});
   var source=ff.StandardOutput.BaseStream;var dest=vlc.StandardInput.BaseStream;var saved=recording;
   Task pump=Task.Run(()=>{
    byte[] buffer=new byte[65536];int n;bool displayOpen=true;
    try{while((n=source.Read(buffer,0,buffer.Length))>0){Interlocked.Add(ref bytes,n);if(saved!=null)saved.Write(buffer,0,n);if(displayOpen){try{dest.Write(buffer,0,n);}catch(IOException){displayOpen=false;}}}}
    finally{try{dest.Close();}catch{}}
   });
   string lastState="waiting";bool receivedFrames=false;Report(c,lastState);
   while(!vlc.HasExited&&!ff.HasExited&&!pump.IsCompleted&&!StopRequested(c)){
    bool ready=Interlocked.Read(ref frames)>=2&&Interlocked.Read(ref bytes)>4096;
    if(ready&&!receivedFrames){receivedFrames=true;ReportDetail(c,".ready","ready");}
    string state=receivedFrames?((unchecked((uint)(Environment.TickCount-(int)Interlocked.Read(ref lastFrameTick)))>4000)?"stalled":"running"):"waiting";
    if(state!=lastState){Report(c,state);lastState=state;}
    Thread.Sleep(100);
   }
   bool closedByUser=vlc.HasExited;
   bool ffAlreadyExited=ff.HasExited;
   bool requestedStop=StopRequested(c);
   if(closedByUser||pump.IsFaulted||requestedStop){Report(c,"stopping");StopFFmpeg(ff);}
   if(!ff.HasExited&&!ff.WaitForExit(5000))StopFFmpeg(ff);
   pump.GetAwaiter().GetResult();errors.GetAwaiter().GetResult();
   if(!receivedFrames&&Interlocked.Read(ref frames)>=2&&Interlocked.Read(ref bytes)>4096)ReportDetail(c,".ready","ready");
   if(recording!=null){recording.Flush(true);recording.Dispose();recording=null;}
   if(!vlc.HasExited&&!vlc.WaitForExit(10000)){vlc.CloseMainWindow();if(!vlc.WaitForExit(3000))vlc.Kill();}
   if(ff.ExitCode!=0&&!requestedStop&&(ffAlreadyExited||!closedByUser)){
    string reason;lock(diagnosticLock)reason=diagnostic.ToString();
    throw new Exception("FFmpeg capture failed ("+ff.ExitCode+").\n"+reason+"Log: "+c.FFmpegLog);
   }
   Report(c,"finalizing");
   FinalizeRecording(c,log);
   Report(c,"complete");
   return 0;
  }catch(Exception e){if(c!=null){ReportDetail(c,".error",e.Message);Report(c,"error");}Console.Error.WriteLine(e.Message);return 1;}
  finally{if(ff!=null){StopFFmpeg(ff);ff.Dispose();}if(vlc!=null){if(!vlc.HasExited){vlc.CloseMainWindow();if(!vlc.WaitForExit(2000))vlc.Kill();}vlc.Dispose();}if(recording!=null)recording.Dispose();if(log!=null)log.Dispose();if(deviceLock!=null){if(ownsDevice){Thread.Sleep(400);deviceLock.ReleaseMutex();}deviceLock.Dispose();}}
 }
}
