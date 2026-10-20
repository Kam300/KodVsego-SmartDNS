using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using HbAi.XboxDns;
static class AppUpdateTests
{
    static int passed;
    static string Response(string wire,int limit=100)
    {using(var input=new MemoryStream(Encoding.ASCII.GetBytes(wire)))using(var output=new MemoryStream()){string location;int status=AppUpdate.ReadResponse(input,output,limit,CancellationToken.None,null,out location);if(status!=200)return status+":"+location;return Encoding.ASCII.GetString(output.ToArray());}}
    static bool RejectResponse(string wire,int limit=100){try{Response(wire,limit);return false;}catch(IOException){return true;}}
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static string Json(string tag="v2.23.0",bool pre=false,string url="https://github.com/Kam300/KodVsego-SmartDNS/releases/download/v2.23.0/KodVsego_SmartDNS.2.23.0.exe",string digest=null)
    {
        return new JavaScriptSerializer().Serialize(new {tag_name=tag,draft=false,prerelease=pre,assets=new[]{new{name="KodVsego_SmartDNS.2.23.0.exe",state="uploaded",size=512000,browser_download_url=url,digest=digest??"sha256:"+new string('a',64)}}});
    }
    static void Main()
    {
        Check(AppUpdate.IsNewer("2.23.0","v2.22.0-test.5"),"New stable version upgrades a test build");
        Check(Response("HTTP/1.1 200 OK\r\nContent-Length: 3\r\n\r\nabc")=="abc","Direct HTTPS reads exact content length");
        Check(Response("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n2;extension=yes\r\nde\r\n0\r\nTrailer: value\r\n\r\n")=="abcde","Direct HTTPS decodes chunked body and bounded trailers");
        Check(Response("HTTP/1.1 200 OK\r\n\r\nabc")=="abc","Direct HTTPS supports connection-close body");
        Check(Response("HTTP/1.1 302 Found\r\nLocation: https://example.com/\r\n\r\n")=="302:https://example.com/","Redirect is returned to caller for allowlist validation");
        Check(RejectResponse("HTTP/1.1 200 OK\r\nContent-Length: 9\r\n\r\nabc"),"Incomplete download is rejected");
        Check(RejectResponse("HTTP/1.1 200 OK\r\nContent-Length: 3\r\n\r\nabc",2),"Declared body cannot exceed size cap");
        Check(RejectResponse("HTTP/1.1 200 OK\r\n\r\nabc",2),"Unframed body also respects size cap");
        Check(RejectResponse("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nff\r\n"),"Oversized chunk is rejected before reading data");
        Check(RejectResponse("HTTP/1.1 200 OK\r\nContent-Length: 3\r\nContent-Length: 4\r\n\r\nabc"),"Duplicate framing headers are rejected");
        Check(RejectResponse("HTTP/1.1 200 OK\r\nContent-Length: 3\r\nTransfer-Encoding: chunked\r\n\r\n"),"Ambiguous response framing is rejected");
        Check(Response("HTTP/1.1 200 OK\r\nSet-Cookie: a\r\nSet-Cookie: b\r\nContent-Length: 3\r\n\r\nabc")=="abc","Repeated non-framing headers do not break a valid download");
        Check(RejectResponse("HTTP/1.1 200 OK\r\nX-Long: "+new string('a',17000)+"\r\n\r\n"),"Response header size is bounded");
        Check(AppUpdate.IsNewer("2.22.0","v2.22.0-test.5"),"Stable release of the same version upgrades prerelease");
        Check(!AppUpdate.IsNewer("2.21.0","v2.22.0-test.5")&&!AppUpdate.IsNewer("2.22.0","v2.22.0"),"Never offer downgrade or reinstall equal stable");
        Check(AppUpdate.Parse(Json(),"2.22.0-test.5").Hash==new string('a',64),"Select correct Windows asset and GitHub SHA-256");
        Check(AppUpdate.Parse(Json(pre:true),"2.22.0-test.5")==null,"Do not silently install prerelease assets");
        Check(AppUpdate.Parse(Json(),"2.24.0")==null,"Old release metadata cannot downgrade current app");
        foreach(string url in new[]{"http://github.com/Kam300/KodVsego-SmartDNS/releases/download/v3/app.exe","https://github.com.evil.test/app.exe","https://github.com/other/repo/releases/download/v3/app.exe","https://user:pass@github.com/Kam300/KodVsego-SmartDNS/releases/download/v3/app.exe","https://github.com:444/Kam300/KodVsego-SmartDNS/releases/download/v3/app.exe","https://127.0.0.1/app.exe"})Check(!AppUpdate.AllowedUrl(url,true),"Reject unsafe update location: "+url);
        Check(AppUpdate.AllowedUrl("https://release-assets.githubusercontent.com/path?signature=value",true)&&!AppUpdate.AllowedUrl("https://release-assets.githubusercontent.com/path"),"Only redirects may use official GitHub asset storage");
        bool refused=false;try{AppUpdate.Parse(Json(url:"https://evil.test/app.exe"),"2.22.0");}catch(InvalidDataException){refused=true;}Check(refused,"Reject foreign download URL in release JSON");
        Check(AppUpdate.Checksum(new string('b',64)+"  other.exe\n"+new string('a',64)+"  wanted.exe","wanted.exe")==new string('a',64),"SHA256SUMS must match exact asset filename");
        refused=false;try{AppUpdate.Checksum(new string('a',64)+"  file.exe\n"+new string('b',64)+"  file.exe","file.exe");}catch(InvalidDataException){refused=true;}Check(refused,"Ambiguous checksum entries are rejected");
        string folder=Path.Combine(Path.GetTempPath(),"SmartDNS-update-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string old=Path.Combine(folder,"current.exe"),package=Path.Combine(folder,"package.exe");
        File.WriteAllText(old,"old harmless test data");File.WriteAllText(package,"new harmless test data");
        string oldHash=AppUpdate.HashFile(old),newHash=AppUpdate.HashFile(package);
        string launched=null;AppUpdate.ReplaceAndLaunch(package,old,oldHash,newHash,path=>launched=path);
        Check(launched==old&&File.ReadAllText(old)=="new harmless test data","Atomic replacement calls launcher only after swap");
        Check(Directory.GetFiles(folder,"*.previous-*").Any(path=>File.ReadAllText(path)=="old harmless test data"),"Original app is retained as rollback backup");
        File.WriteAllText(old,"old again");oldHash=AppUpdate.HashFile(old);
        try{AppUpdate.ReplaceAndLaunch(package,old,oldHash,newHash,path=>{throw new IOException("simulated launch failure");});}catch(IOException){}
        Check(File.ReadAllText(old)=="old again","Launch failure restores previous app bytes");
        File.WriteAllText(package,"tampered");refused=false;
        try{AppUpdate.ReplaceAndLaunch(package,old,oldHash,newHash,path=>{throw new Exception("Must not execute");});}catch(IOException){refused=true;}
        Check(refused&&File.ReadAllText(old)=="old again","Changed package never replaces or launches");
        refused=false;try{AppUpdate.ValidatePackage(package,new UpdateRelease {Hash=newHash,Size=8,Version="2.23.0"});}catch(InvalidDataException){refused=true;}Check(refused,"Download validation rejects size and digest mismatch");
        var request=new UpdateRequest {Parent=42,ParentStarted=12,Target=old,OriginalHash=oldHash,PackageHash=newHash};
        Check(!AppUpdate.ValidRequest(request,package),"Installer cannot run outside its private staging location");
        // Delete only this known test directory, never the real application or update cache.
        foreach(string file in Directory.GetFiles(folder))File.Delete(file);Directory.Delete(folder);
        Console.WriteLine("All "+passed+" updater checks passed; dummy files only, no real app replaced or launched.");
    }
}
