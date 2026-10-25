using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace HbAi.XboxDns
{
    sealed class UpdateOfflineException : IOException
    {
        internal UpdateOfflineException() : base("No usable network connection before update request") { }
    }
    sealed class UpdateElevationException : IOException
    {
        internal UpdateElevationException() : base("Self-update requires an unelevated process") { }
    }
    sealed class UpdateHttpException : IOException
    {
        internal readonly int Status;
        internal UpdateHttpException(int status,Exception inner=null) : base("Update HTTP "+status,inner) {Status=status;}
    }
    sealed class UpdateProblem
    {
        internal string Caption, Message, Action;
        internal static UpdateProblem Describe(Exception error,bool download,bool connected)
        {
            string caption=null,message=null;bool network=false;
            for(var cause=error;cause!=null;cause=cause.InnerException) {
                if(cause is UpdateOfflineException)return Offline(download);
                if(cause is UpdateElevationException)return new UpdateProblem {Caption="Нужен обычный запуск",Message="Для обновления закройте Smart DNS и запустите его без прав администратора.",Action="Повторить загрузку"};
                var http=cause as UpdateHttpException;
                if(http!=null) {
                    caption=http.Status==429?"Слишком много запросов":http.Status==403?"GitHub отклонил запрос":http.Status==404?"Релиз не найден":"Ошибка сервера обновлений";
                    message=http.Status==429?"GitHub ограничил частоту запросов. Подождите и повторите попытку.":http.Status==403?"GitHub отказал в доступе к обновлению. Попробуйте позже или откройте страницу релизов в браузере.":http.Status==404?"Запрошенный релиз или файл недоступен. Повторите проверку позже.":"Сервер обновлений временно не отвечает корректно. Повторите попытку позже.";
                    break;
                }
                if(cause is InvalidDataException) {
                    caption=download?"Файл не прошёл проверку":"Некорректный ответ сервера";
                    message=download?"Загруженный файл не прошёл проверку. Повторите загрузку. Текущая программа не изменена.":"Не удалось прочитать сведения о выпуске. Повторите проверку позже.";break;
                }
                if(cause is UnauthorizedAccessException||cause is System.Security.SecurityException) {
                    caption="Нет доступа к файлам";message="Не удалось сохранить или подготовить обновление. Проверьте доступ к папке программы и повторите попытку.";break;
                }
                // WinHTTP codes: https://learn.microsoft.com/windows/win32/winhttp/error-messages
                var native=cause as Win32Exception;
                var socket=cause as SocketException;
                var web=cause as WebException;
                int code=socket!=null?socket.ErrorCode:native==null?0:native.NativeErrorCode;
                if(cause is AuthenticationException||code==12037||code==12038||code==12045||code==12057||code==12157||code==12169||code==12170||code==12175||code==12179||web!=null&&(web.Status==WebExceptionStatus.TrustFailure||web.Status==WebExceptionStatus.SecureChannelFailure)) {
                    caption="Не удалось проверить HTTPS";message="Не удалось установить защищённое соединение с сервером обновлений. Проверьте дату и время Windows и повторите попытку.";network=true;break;
                }
                if(cause is TimeoutException||code==12002||code==10060||web!=null&&web.Status==WebExceptionStatus.Timeout) {
                    caption="Сервер не ответил вовремя";message="Соединение с сервером обновлений заняло слишком много времени. Проверьте подключение и повторите попытку.";network=true;break;
                }
                if(code==12007||code==11001||code==11002||web!=null&&(web.Status==WebExceptionStatus.NameResolutionFailure||web.Status==WebExceptionStatus.ProxyNameResolutionFailure)) {
                    caption="Не удалось найти сервер";message="Не удалось получить адрес сервера обновлений. Проверьте подключение и настройки DNS, затем повторите попытку.";network=true;break;
                }
                if(socket!=null||code==12029||code==12030||code==12031||web!=null) {
                    caption="Нет соединения с GitHub";message="Не удалось связаться с сервером обновлений. Проверьте доступ к GitHub и повторите попытку.";network=true;
                }
            }
            if(network&&!connected)return Offline(download);
            return new UpdateProblem {Caption=caption??(download?"Не удалось скачать обновление":"Не удалось проверить обновления"),Message=message??"Повторите попытку. Подробности ошибки доступны в журнале программы в настройках.",Action=download?"Повторить загрузку":"Повторить проверку"};
        }
        static UpdateProblem Offline(bool download)
        {return new UpdateProblem {Caption="Нет сетевого подключения",Message="Подключитесь к Wi-Fi или Ethernet и повторите "+(download?"загрузку.":"проверку.")+"\nТекущая программа не изменена.",Action=download?"Повторить загрузку":"Повторить проверку"};}
    }
    sealed class UpdateRelease
    {
        internal string Version, Name, Url, Hash, ChecksumUrl;
        internal long Size;
    }
    public sealed class UpdateRequest
    {
        public int Parent {get;set;}
        public long ParentStarted {get;set;}
        public string Target {get;set;}
        public string OriginalHash {get;set;}
        public string PackageHash {get;set;}
    }
    sealed class UpdateTicket
    {
        internal string Directory;
        internal void Commit() {File.WriteAllText(Path.Combine(Directory,"commit"),"install");}
        internal void Cancel() {File.WriteAllText(Path.Combine(Directory,"cancel"),"cancel");}
    }
    static class AppUpdate
    {
        internal const string MsiMarkerName="SmartDNS.Msi.json";
        internal static bool IsMsiManaged(string executable)
        {return File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable)),MsiMarkerName));}
        internal static void EnsureSelfUpdateAllowed(string executable)
        {if(IsMsiManaged(executable))throw new IOException("Эта копия установлена через MSI. Обновите её установщиком MSI со страницы релизов.");}
        internal static string MsiReleasePage(string version)
        {
            if(!Regex.IsMatch(version??"",@"^\d+\.\d+\.\d+$"))throw new InvalidDataException("Некорректный номер версии MSI.");
            return "https://github.com/Kam300/KodVsego-SmartDNS/releases/tag/v"+version;
        }
        internal const string LatestApi="https://api.github.com/repos/Kam300/KodVsego-SmartDNS/releases/latest";
        internal const long MaxPackage=64*1024*1024;
        internal static string CacheRoot {get {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"KodVsego","SmartDNS","Updates");}}
        static string Text(Dictionary<string,object> row,string key) {object value;return row.TryGetValue(key,out value)?Convert.ToString(value):"";}
        static Version CoreVersion(string value)
        {
            string core=(value??"").TrimStart('v').Split('-')[0]; Version version;
            if(!Regex.IsMatch(core,@"^\d+\.\d+\.\d+(\.\d+)?$")||!Version.TryParse(core,out version))throw new InvalidDataException("Некорректный номер версии.");
            return new Version(version.Major,version.Minor,version.Build,Math.Max(0,version.Revision));
        }
        internal static bool IsNewer(string latest,string current)
        {int comparison=CoreVersion(latest).CompareTo(CoreVersion(current));return comparison>0||comparison==0&&current.Contains("-")&&!latest.Contains("-");}
        internal static bool AllowedUrl(string value,bool redirect=false)
        {
            Uri uri;
            if(!Uri.TryCreate(value,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.Port!=443||uri.UserInfo!=""||uri.Fragment!="")return false;
            if(uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase))
                return uri.AbsolutePath.StartsWith("/Kam300/KodVsego-SmartDNS/releases/download/",StringComparison.OrdinalIgnoreCase);
            return redirect&&(uri.Host.Equals("release-assets.githubusercontent.com",StringComparison.OrdinalIgnoreCase)||uri.Host.Equals("objects.githubusercontent.com",StringComparison.OrdinalIgnoreCase));
        }
        internal static UpdateRelease Parse(string json,string current)
        {
            var data=new JavaScriptSerializer {MaxJsonLength=1024*1024}.DeserializeObject(json) as Dictionary<string,object>;
            if(data==null||!data.ContainsKey("draft")||!data.ContainsKey("prerelease"))throw new InvalidDataException("Не удалось прочитать сведения об обновлении.");
            if(Convert.ToBoolean(data["draft"])||Convert.ToBoolean(data["prerelease"]))return null;
            string version=Text(data,"tag_name").TrimStart('v');
            if(version.Contains("-")||!IsNewer(version,current))return null;
            object raw;var assets=data.TryGetValue("assets",out raw)?raw as object[]:null;
            if(assets==null)return null;
            string wanted="KodVsego_SmartDNS."+version+".exe";
            var rows=assets.OfType<Dictionary<string,object>>().ToArray();
            var matching=rows.Where(row=>Text(row,"name")==wanted&&Text(row,"state")=="uploaded").ToArray();
            if(matching.Length!=1)return null;
            var asset=matching[0];long size;
            if(!long.TryParse(Text(asset,"size"),out size)||size<=0||size>MaxPackage||!AllowedUrl(Text(asset,"browser_download_url")))throw new InvalidDataException("Некорректный файл обновления.");
            string digest=Text(asset,"digest"),hash="";
            if(digest.StartsWith("sha256:",StringComparison.Ordinal)&&Regex.IsMatch(digest.Substring(7),"^[a-fA-F0-9]{64}$"))hash=digest.Substring(7).ToLowerInvariant();
            var sums=rows.SingleOrDefault(row=>Text(row,"name")=="SHA256SUMS.txt"&&Text(row,"state")=="uploaded");
            string checksum=sums==null?"":Text(sums,"browser_download_url");
            if(hash==""&&!AllowedUrl(checksum))throw new InvalidDataException("У обновления нет проверочной суммы SHA-256.");
            return new UpdateRelease {Version=version,Name=wanted,Url=Text(asset,"browser_download_url"),Size=size,Hash=hash,ChecksumUrl=checksum};
        }
        static void Receive(string url,Stream output,long limit,CancellationToken cancel,Action<long> progress=null,bool metadata=false)
        {
            long position=output.Position;
            try {ReceiveSystem(url,output,limit,cancel,progress,metadata);}
            catch(WebException ex) {
                cancel.ThrowIfCancellationRequested();
                if(ex.Status==WebExceptionStatus.ProtocolError) {
                    using(var response=ex.Response as HttpWebResponse) {
                        if(response!=null)throw new UpdateHttpException((int)response.StatusCode,ex);
                    }
                    throw;
                }
                output.SetLength(position);output.Position=position;
                ReceiveDirect(url,output,limit,cancel,progress,metadata);
            }
        }
        static void ReceiveSystem(string url,Stream output,long limit,CancellationToken cancel,Action<long> progress,bool metadata)
        {
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            for(int hop=0;hop<6;hop++) {
                if(!(metadata?url==LatestApi:AllowedUrl(url,hop>0)))throw new IOException("Недопустимый адрес обновления.");
                var request=(HttpWebRequest)WebRequest.Create(url);
                request.UserAgent="KodVsego-SmartDNS/"+MainForm.VersionLabel;
                request.Accept=metadata?"application/vnd.github+json":"application/octet-stream";
                request.AllowAutoRedirect=false;request.UseDefaultCredentials=false;
                request.Timeout=5000;request.ReadWriteTimeout=12000;
                using(cancel.Register(()=>request.Abort())) {
                    cancel.ThrowIfCancellationRequested();
                    using(var response=(HttpWebResponse)request.GetResponse()) {
                        int status=(int)response.StatusCode;
                        if(status>=300&&status<400) {
                            Uri destination;
                            if(metadata||!Uri.TryCreate(new Uri(url),response.Headers["Location"],out destination))throw new IOException("Неожиданное перенаправление обновления.");
                            url=destination.AbsoluteUri;continue;
                        }
                        if(response.StatusCode!=HttpStatusCode.OK)throw new UpdateHttpException(status);
                        if(response.ContentLength>limit)throw new IOException("Файл обновления слишком большой.");
                        using(var input=response.GetResponseStream()) {
                            var buffer=new byte[32768];long total=0;int count;
                            while((count=input.Read(buffer,0,buffer.Length))>0) {
                                cancel.ThrowIfCancellationRequested();total+=count;
                                if(total>limit)throw new IOException("Превышен размер файла обновления.");
                                output.Write(buffer,0,count);if(progress!=null)progress(total);
                            }
                        }
                        return;
                    }
                }
            }
            throw new IOException("Слишком много перенаправлений обновления.");
        }
        static string HeaderLine(Stream input,CancellationToken cancel,ref int budget)
        {
            var line=new StringBuilder();
            while(budget-->0) {
                cancel.ThrowIfCancellationRequested();int c=input.ReadByte();
                if(c<0)throw new IOException("Неполный ответ сервера обновлений.");
                if(c=='\n') {if(line.Length==0||line[line.Length-1]!='\r')throw new IOException("Некорректный ответ HTTPS.");return line.ToString(0,line.Length-1);}
                line.Append((char)c);
            }
            throw new IOException("Заголовки ответа слишком большие.");
        }
        static void CopyBody(Stream input,Stream output,long count,long limit,CancellationToken cancel,Action<long> progress,ref long total)
        {
            var buffer=new byte[32768];
            while(count!=0) {
                cancel.ThrowIfCancellationRequested();
                int read=input.Read(buffer,0,count<0?buffer.Length:(int)Math.Min(count,buffer.Length));
                if(read==0){if(count>0)throw new IOException("Загрузка прервана до конца файла.");return;}
                total+=read;if(total>limit)throw new IOException("Превышен размер файла обновления.");
                output.Write(buffer,0,read);if(progress!=null)progress(total);
                if(count>0)count-=read;
            }
        }
        internal static int ReadResponse(Stream input,Stream output,long limit,CancellationToken cancel,Action<long> progress,out string location)
        {
            int budget=16384,status;var first=HeaderLine(input,cancel,ref budget).Split(' ');
            if(first.Length<2||!first[0].StartsWith("HTTP/1.")||!int.TryParse(first[1],out status))throw new IOException("Некорректный ответ HTTPS.");
            var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);string line;
            while((line=HeaderLine(input,cancel,ref budget))!="") {
                int colon=line.IndexOf(':');if(colon<=0)throw new IOException("Некорректный заголовок HTTPS.");
                string key=line.Substring(0,colon).Trim();
                if(headers.ContainsKey(key)) {
                    if(new[]{"Content-Length","Transfer-Encoding","Content-Encoding","Location"}.Contains(key,StringComparer.OrdinalIgnoreCase))throw new IOException("Повторяющийся заголовок HTTPS.");
                    continue;
                }
                headers[key]=line.Substring(colon+1).Trim();
            }
            headers.TryGetValue("Location",out location);
            if(status!=200)return status;
            string encoding;if(headers.TryGetValue("Content-Encoding",out encoding)&&encoding!="identity")throw new IOException("Неожиданное сжатие ответа.");
            long total=0,length;string value;
            if(headers.TryGetValue("Transfer-Encoding",out value)) {
                if(!value.Equals("chunked",StringComparison.OrdinalIgnoreCase)||headers.ContainsKey("Content-Length"))throw new IOException("Неоднозначная длина ответа HTTPS.");
                while(true) {
                    budget=128;string chunk=HeaderLine(input,cancel,ref budget).Split(';')[0];
                    if(!long.TryParse(chunk,System.Globalization.NumberStyles.AllowHexSpecifier,System.Globalization.CultureInfo.InvariantCulture,out length)||length<0||length>limit-total)throw new IOException("Некорректный размер блока обновления.");
                    if(length==0){budget=8192;while(HeaderLine(input,cancel,ref budget)!=""){}return status;}
                    CopyBody(input,output,length,limit,cancel,progress,ref total);
                    budget=2;if(HeaderLine(input,cancel,ref budget)!="")throw new IOException("Некорректный блок HTTPS.");
                }
            }
            if(headers.TryGetValue("Content-Length",out value)) {
                if(!long.TryParse(value,out length)||length<0||length>limit)throw new IOException("Некорректный размер файла обновления.");
            } else length=-1;
            CopyBody(input,output,length,limit,cancel,progress,ref total);return status;
        }
        static void ReceiveDirect(string url,Stream output,long limit,CancellationToken cancel,Action<long> progress,bool metadata)
        {
            for(int hop=0;hop<6;hop++) {
                cancel.ThrowIfCancellationRequested();
                if(!(metadata?url==LatestApi:AllowedUrl(url,hop>0)))throw new IOException("Недопустимый адрес обновления.");
                var uri=new Uri(url);var query=DnsProbe.Query(uri.Host);
                // Bootstrap by IP so a Smart DNS rewrite of GitHub cannot also rewrite the resolver.
                var response=DohHttp2.Query("https://1.1.1.1/dns-query",query);
                var addresses=DnsProbe.Addresses(query,response).Where(AutoDnsProbe.PublicAddress).Take(4).ToArray();
                Exception failure=null;string location=null;int status=0;bool received=false;
                foreach(string address in addresses) {
                    long position=output.Position;
                    try {
                        using(var tcp=new TcpClient(AddressFamily.InterNetwork))
                        using(cancel.Register(()=>tcp.Close())) {
                            var connect=tcp.BeginConnect(IPAddress.Parse(address),443,null,null);
                            using(var wait=connect.AsyncWaitHandle)if(!wait.WaitOne(8000))throw new TimeoutException("GitHub connect timeout");
                            tcp.EndConnect(connect);cancel.ThrowIfCancellationRequested();
                            using(var ssl=new SslStream(tcp.GetStream(),false)) {
                                ssl.ReadTimeout=12000;ssl.WriteTimeout=12000;
                                var auth=ssl.BeginAuthenticateAsClient(uri.Host,null,SslProtocols.Tls12,true,null,null);
                                using(var wait=auth.AsyncWaitHandle)if(!wait.WaitOne(8000))throw new TimeoutException("GitHub TLS timeout");
                                ssl.EndAuthenticateAsClient(auth);cancel.ThrowIfCancellationRequested();
                                byte[] request=Encoding.ASCII.GetBytes("GET "+uri.PathAndQuery+" HTTP/1.1\r\nHost: "+uri.Host+"\r\nUser-Agent: KodVsego-SmartDNS\r\nAccept: "+(metadata?"application/vnd.github+json":"application/octet-stream")+"\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n");
                                ssl.Write(request,0,request.Length);
                                status=ReadResponse(ssl,output,limit,cancel,progress,out location);received=true;break;
                            }
                        }
                    }
                    catch(Exception ex) {
                        cancel.ThrowIfCancellationRequested();if(!(ex is IOException||ex is SocketException||ex is AuthenticationException||ex is TimeoutException))throw;
                        failure=ex;output.SetLength(position);output.Position=position;
                    }
                }
                if(!received)throw new IOException("Нет защищённого соединения с GitHub. Повторите попытку позже.",failure);
                if(status==200)return;
                if(status>=300&&status<400&&!metadata) {Uri next;if(Uri.TryCreate(uri,location,out next)){url=next.AbsoluteUri;continue;}}
                throw new UpdateHttpException(status);
            }
            throw new IOException("Слишком много перенаправлений обновления.");
        }
        static string FetchText(string url,int limit,CancellationToken cancel,bool metadata=false)
        {using(var stream=new MemoryStream()){Receive(url,stream,limit,cancel,null,metadata);return Encoding.UTF8.GetString(stream.ToArray());}}
        internal static UpdateRelease Check(string current,CancellationToken cancel)
        {return Check(current,cancel,NetworkCheck.HasConnection,token=>FetchText(LatestApi,1024*1024,token,true));}
        internal static void RequireConnection(CancellationToken cancel,Func<bool> connected)
        {cancel.ThrowIfCancellationRequested();if(!connected())throw new UpdateOfflineException();}
        internal static UpdateRelease Check(string current,CancellationToken cancel,Func<bool> connected,Func<CancellationToken,string> fetch)
        {RequireConnection(cancel,connected);return Parse(fetch(cancel),current);}
        internal static string Checksum(string text,string name)
        {
            var matches=text.Split('\n').Select(line=>Regex.Match(line.Trim(),@"^([a-fA-F0-9]{64})\s+\*?(.+)$"))
                .Where(m=>m.Success&&m.Groups[2].Value==name).ToArray();
            if(matches.Length!=1)throw new InvalidDataException("Не найдена однозначная проверочная сумма EXE.");
            return matches[0].Groups[1].Value.ToLowerInvariant();
        }
        internal static string HashFile(string file)
        {using(var input=File.OpenRead(file))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();}
        internal static void ValidatePackage(string file,UpdateRelease release)
        {
            if(new FileInfo(file).Length!=release.Size||!Regex.IsMatch(release.Hash??"","^[a-fA-F0-9]{64}$")||HashFile(file)!=release.Hash.ToLowerInvariant())throw new InvalidDataException("Проверочная сумма обновления не совпала. Текущая программа не изменена.");
            var info=FileVersionInfo.GetVersionInfo(file);
            if(info.ProductName!="Код Всего · Smart DNS"||info.ProductVersion==null||info.ProductVersion.Contains("-")||CoreVersion(info.ProductVersion)!=CoreVersion(release.Version))throw new InvalidDataException("Файл не соответствует версии Smart DNS.");
            AssemblyName.GetAssemblyName(file); // Read managed metadata without executing the downloaded program.
        }
        internal static string Download(UpdateRelease release,CancellationToken cancel,Action<int> progress)
        {
            RequireConnection(cancel,NetworkCheck.HasConnection);
            if(string.IsNullOrEmpty(release.Hash))release.Hash=Checksum(FetchText(release.ChecksumUrl,65536,cancel),release.Name);
            string directory=Path.Combine(CacheRoot,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            string package=Path.Combine(directory,"update.exe");
            using(var output=new FileStream(package,FileMode.CreateNew,FileAccess.Write,FileShare.None,32768,FileOptions.WriteThrough)) {
                Receive(release.Url,output,release.Size,cancel,n=>{if(progress!=null)progress((int)(n*100/release.Size));});output.Flush(true);
            }
            cancel.ThrowIfCancellationRequested();ValidatePackage(package,release);return package;
        }
        internal static bool IsStagedPackage(string file)
        {
            if(string.IsNullOrEmpty(file))return false;
            try {
                string full=Path.GetFullPath(file),directory=Path.GetDirectoryName(full);Guid token;
                return full.Equals(file,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full)=="update.exe"&&
                    Guid.TryParseExact(Path.GetFileName(directory),"N",out token)&&
                    string.Equals(Path.GetDirectoryName(directory),CacheRoot,StringComparison.OrdinalIgnoreCase);
            }catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(PathTooLongException){return false;}
        }
        internal static FileStream OpenValidatedPackage(string package,UpdateRelease release,CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if(!IsStagedPackage(package)||release==null||release.Size<=0||release.Size>MaxPackage)
                throw new InvalidDataException("Некорректный пакет обновления. Текущая программа не изменена.");
            // Keep the verified executable read-only until its helper confirms startup.
            // This is a repeat check against the release digest, not a new digest of arbitrary bytes.
            var locked=new FileStream(package,FileMode.Open,FileAccess.Read,FileShare.Read);
            try {
                if((File.GetAttributes(package)&FileAttributes.ReparsePoint)!=0||
                    (File.GetAttributes(Path.GetDirectoryName(package))&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("Пакет обновления перенаправлен. Запуск отменён.");
                ValidatePackage(package,release);cancel.ThrowIfCancellationRequested();return locked;
            }catch{locked.Dispose();throw;}
        }
        internal static UpdateTicket StartInstall(string package,UpdateRelease release,CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            EnsureSelfUpdateAllowed(Application.ExecutablePath);
            if(DnsElevation.IsAdministrator())throw new UpdateElevationException();
            if(release==null)throw new InvalidDataException("Не найдены сведения о проверенном обновлении.");
            var expected=new UpdateRelease {Version=release.Version,Hash=release.Hash,Size=release.Size};
            using(var locked=OpenValidatedPackage(package,expected,cancel)) {
                string directory=Path.GetDirectoryName(package),target=Application.ExecutablePath;
                using(var parent=Process.GetCurrentProcess()) {
                    var request=new UpdateRequest {Parent=parent.Id,ParentStarted=parent.StartTime.ToUniversalTime().Ticks,Target=target,OriginalHash=HashFile(target),PackageHash=expected.Hash.ToLowerInvariant()};
                    File.WriteAllText(Path.Combine(directory,"request.json"),new JavaScriptSerializer().Serialize(request),new UTF8Encoding(false));
                }
                using(var helper=Process.Start(new ProcessStartInfo(package,"--install-update") {UseShellExecute=false,WorkingDirectory=Environment.GetFolderPath(Environment.SpecialFolder.System)})) {
                    try {
                        if(helper==null)throw new IOException("Не удалось запустить установку обновления.");
                        for(int i=0;i<100;i++) {
                            cancel.ThrowIfCancellationRequested();
                            if(File.Exists(Path.Combine(directory,"ready")))return new UpdateTicket {Directory=directory};
                            if(helper.HasExited)throw new IOException("Не удалось подготовить установку обновления.");
                            Thread.Sleep(100);
                        }
                        throw new IOException("Установщик обновления не ответил.");
                    }
                    catch {File.WriteAllText(Path.Combine(directory,"cancel"),"cancel");throw;}
                }
            }
        }
        internal static bool ValidRequest(UpdateRequest request,string helper)
        {
            if(request==null||request.Parent<=0||request.ParentStarted<=0||!Regex.IsMatch(request.OriginalHash??"","^[a-f0-9]{64}$")||!Regex.IsMatch(request.PackageHash??"","^[a-f0-9]{64}$"))return false;
            string target;
            try{target=Path.GetFullPath(request.Target);}catch{return false;}
            string fileName=Path.GetFileName(target);
            return target==request.Target&&IsStagedPackage(helper)&&!IsMsiManaged(target)&&
                (fileName.Equals("SmartDNS.exe",StringComparison.OrdinalIgnoreCase)||Regex.IsMatch(fileName,@"^KodVsego_SmartDNS\.[A-Za-z0-9.-]+\.exe$"))&&
                !target.Equals(helper,StringComparison.OrdinalIgnoreCase);
        }
        internal static void ReplaceAndLaunch(string package,string target,string originalHash,string packageHash,Action<string> launch)
        {
            EnsureSelfUpdateAllowed(target);
            if(HashFile(package)!=packageHash||HashFile(target)!=originalHash)throw new IOException("Файл программы изменился. Повторите обновление.");
            string staged=target+".update-"+Guid.NewGuid().ToString("N"),backup=target+".previous-"+Guid.NewGuid().ToString("N");
            File.Copy(package,staged,false);
            if(HashFile(staged)!=packageHash)throw new IOException("Ошибка копирования обновления.");
            File.Replace(staged,target,backup);
            try{launch(target);}
            catch {File.Replace(backup,target,staged);throw;}
        }
        internal static int Install()
        {
            try {
                if(DnsElevation.IsAdministrator())return 4;
                string helper=Application.ExecutablePath,directory=Path.GetDirectoryName(helper),json=Path.Combine(directory,"request.json");
                if(!File.Exists(json)||new FileInfo(json).Length>8192)return 4;
                var request=new JavaScriptSerializer().Deserialize<UpdateRequest>(File.ReadAllText(json));
                if(!ValidRequest(request,helper)||HashFile(helper)!=request.PackageHash)return 4;
                if((File.GetAttributes(request.Target)&FileAttributes.ReparsePoint)!=0||FileVersionInfo.GetVersionInfo(request.Target).ProductName!="Код Всего · Smart DNS")return 4;
                using(var parent=Process.GetProcessById(request.Parent)) {
                    if(parent.StartTime.ToUniversalTime().Ticks!=request.ParentStarted||!ProcessSecurity.ImagePath(parent.Id).Equals(request.Target,StringComparison.OrdinalIgnoreCase)||HashFile(request.Target)!=request.OriginalHash)return 4;
                    using(var ready=new FileStream(Path.Combine(directory,"ready"),FileMode.CreateNew,FileAccess.Write)){ready.WriteByte(1);ready.Flush(true);}
                    bool committed=false;
                    for(int i=0;i<120;i++) {
                        if(File.Exists(Path.Combine(directory,"cancel")))return 4;
                        if(File.Exists(Path.Combine(directory,"commit"))){committed=true;break;}
                        if(parent.HasExited)return 4;
                        Thread.Sleep(100);
                    }
                    if(!committed||!parent.WaitForExit(30000))return 4;
                }
                ReplaceAndLaunch(helper,request.Target,request.OriginalHash,request.PackageHash,path=> {
                    using(var started=Process.Start(new ProcessStartInfo(path){UseShellExecute=false,WorkingDirectory=Environment.GetFolderPath(Environment.SpecialFolder.System)}))if(started==null)throw new IOException("Новая версия не запустилась.");
                });
                return 0;
            }
            catch(Exception ex) {MessageBox.Show("Не удалось завершить обновление. Закройте другие копии программы и повторите попытку.\n\n"+ex.Message,"Обновление Smart DNS",MessageBoxButtons.OK,MessageBoxIcon.Warning);return 4;}
        }
    }
}
