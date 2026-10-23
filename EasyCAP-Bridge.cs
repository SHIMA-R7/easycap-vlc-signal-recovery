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
}
internal static class EasyCAPBridge {
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
  Process ff=null,vlc=null;FileStream recording=null;StreamWriter log=null;
  try {
   if(args.Length!=1)throw new ArgumentException("Expected bridge configuration path.");
   var c=new JavaScriptSerializer().Deserialize<BridgeConfig>(File.ReadAllText(args[0],Encoding.UTF8));
   log=new StreamWriter(c.FFmpegLog,false,new UTF8Encoding(false));log.AutoFlush=true;
   if(!string.IsNullOrEmpty(c.RecordingPath))recording=new FileStream(c.RecordingPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
   var vi=Info(c.VLC,c.VLCArguments);vi.RedirectStandardInput=true;
   vlc=Process.Start(vi);
   var fi=Info(c.FFmpeg,c.FFmpegArguments);fi.RedirectStandardInput=true;fi.RedirectStandardOutput=true;fi.RedirectStandardError=true;
   ff=Process.Start(fi);
   var logger=log;
   Task errors=Task.Run(()=>{string line;while((line=ff.StandardError.ReadLine())!=null)logger.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ")+line);});
   var source=ff.StandardOutput.BaseStream;var dest=vlc.StandardInput.BaseStream;var saved=recording;
   Task pump=Task.Run(()=>{
    byte[] buffer=new byte[65536];int n;bool displayOpen=true;
    try{while((n=source.Read(buffer,0,buffer.Length))>0){if(saved!=null)saved.Write(buffer,0,n);if(displayOpen){try{dest.Write(buffer,0,n);}catch(IOException){displayOpen=false;}}}}
    finally{try{dest.Close();}catch{}}
   });
   while(!vlc.HasExited&&!ff.HasExited&&!pump.IsCompleted)Thread.Sleep(100);
   bool closedByUser=vlc.HasExited;
   if(closedByUser||pump.IsFaulted)StopFFmpeg(ff);
   if(!ff.HasExited&&!ff.WaitForExit(5000))StopFFmpeg(ff);
   pump.GetAwaiter().GetResult();errors.GetAwaiter().GetResult();
   if(recording!=null){recording.Flush(true);recording.Dispose();recording=null;}
   if(!vlc.HasExited&&!vlc.WaitForExit(10000)){vlc.CloseMainWindow();if(!vlc.WaitForExit(3000))vlc.Kill();}
   if(ff.ExitCode!=0&&!closedByUser)throw new Exception("FFmpeg capture failed ("+ff.ExitCode+"). See "+c.FFmpegLog);
   FinalizeRecording(c,log);
   return 0;
  }catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}
  finally{if(ff!=null){StopFFmpeg(ff);ff.Dispose();}if(vlc!=null){if(!vlc.HasExited){vlc.CloseMainWindow();if(!vlc.WaitForExit(2000))vlc.Kill();}vlc.Dispose();}if(recording!=null)recording.Dispose();if(log!=null)log.Dispose();}
 }
}
