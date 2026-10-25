using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Xml;

static class ArtifactInspection {
    const uint DataFile=2,ImageResource=32;
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern IntPtr LoadLibraryExW(string name,IntPtr reserved,uint flags);
    [DllImport("kernel32.dll",SetLastError=true,ExactSpelling=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll",SetLastError=true,ExactSpelling=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern IntPtr FindResourceW(IntPtr module,IntPtr name,IntPtr type);
    [DllImport("kernel32.dll",SetLastError=true,ExactSpelling=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern IntPtr LoadResource(IntPtr module,IntPtr resource);
    [DllImport("kernel32.dll",SetLastError=true,ExactSpelling=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll",SetLastError=true,ExactSpelling=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern uint SizeofResource(IntPtr module,IntPtr resource);
    static readonly Dictionary<string,string> AllowedReferences=new Dictionary<string,string>(StringComparer.Ordinal) {
        {"mscorlib","b77a5c561934e089"},{"System","b77a5c561934e089"},{"System.Core","b77a5c561934e089"},
        {"System.Drawing","b03f5f7f11d50a3a"},{"System.Windows.Forms","b77a5c561934e089"},{"System.Management","b03f5f7f11d50a3a"},{"System.Web.Extensions","31bf3856ad364e35"},{"Microsoft.Management.Infrastructure","31bf3856ad364e35"},
        // Current-user DPAPI protection for the local Gemini API credential.
        {"System.Security","b03f5f7f11d50a3a"}
    };
    static readonly string[] AllowedDlls={"kernel32.dll","user32.dll","shell32.dll","rstrtmgr.dll","iphlpapi.dll","dnsapi.dll","ntdll.dll","winhttp.dll"};
    // This application has no feature that needs explicit foreign-process memory/thread primitives.
    // This checks declared imports, not APIs used internally by the CLR or indirect managed calls.
    static readonly string[] ReviewRequiredEntryPoints={"ReadProcessMemory","WriteProcessMemory","VirtualAllocEx","VirtualProtectEx","CreateRemoteThread","CreateRemoteThreadEx","NtCreateThreadEx","NtReadVirtualMemory","NtWriteVirtualMemory","QueueUserAPC","SetThreadContext"};
    static readonly string[] AllowedResources={"licenses.MIT","licenses.Russian","licenses.OFL-Unbounded","licenses.OFL-Manrope","licenses.HB-AI-Russian","app.ico","Unbounded-ExtraBold.ttf","Manrope-Bold.ttf","Manrope-Medium.ttf","service-icons.chatgpt.png","service-icons.claude.png","service-icons.gemini.png","service-icons.elevenlabs.png","service-icons.xbox.png","service-icons.spotify.png"};
    static string Hex(byte[] bytes){return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
    static string Hash(byte[] bytes){using(var sha=SHA256.Create())return Hex(sha.ComputeHash(bytes));}
    static string Manifest(string file) {
        IntPtr module=LoadLibraryExW(file,IntPtr.Zero,DataFile|ImageResource);
        if(module==IntPtr.Zero)throw new InvalidDataException("Cannot map PE as read-only data");
        try {
            IntPtr resource=FindResourceW(module,(IntPtr)1,(IntPtr)24);
            if(resource==IntPtr.Zero)throw new InvalidDataException("Missing application manifest");
            uint size=SizeofResource(module,resource);
            if(size==0||size>1024*1024)throw new InvalidDataException("Invalid manifest length");
            IntPtr data=LockResource(LoadResource(module,resource));
            if(data==IntPtr.Zero)throw new InvalidDataException("Unreadable manifest");
            byte[] bytes=new byte[size];Marshal.Copy(data,bytes,0,bytes.Length);
            var settings=new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null};
            var document=new XmlDocument {XmlResolver=null};
            using(var stream=new MemoryStream(bytes))using(var reader=XmlReader.Create(stream,settings))document.Load(reader);
            var levels=document.SelectNodes("//*[local-name()='requestedExecutionLevel']");
            if(levels.Count!=1||levels[0].Attributes["level"]==null||levels[0].Attributes["level"].Value!="asInvoker"||levels[0].Attributes["uiAccess"]==null||levels[0].Attributes["uiAccess"].Value!="false")throw new InvalidDataException("Manifest must use asInvoker and uiAccess=false");
            return "asInvoker";
        } finally {FreeLibrary(module);}
    }
    static object Inspect(string file) {
        byte[] bytes=File.ReadAllBytes(file);
        var assembly=Assembly.ReflectionOnlyLoad(bytes);
        if(assembly.EntryPoint==null)throw new InvalidDataException("Missing managed entry point");
        var references=assembly.GetReferencedAssemblies();
        foreach(var reference in references){string token;
            if(!AllowedReferences.TryGetValue(reference.Name,out token)||Hex(reference.GetPublicKeyToken())!=token||reference.Version!=new Version(reference.Name=="Microsoft.Management.Infrastructure"?"1.0.0.0":"4.0.0.0"))throw new InvalidDataException("Unapproved assembly reference: "+reference.FullName);
        }
        var resources=new List<object>();
        var nativeResources=new Dictionary<string,string>{{"native.wintun.x64","e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce"},{"native.wintun.x86","d694fa46ab4cfebcb2632d094c7aa97278eef2f8052438621766d863ae98a931"}};
        foreach(string name in assembly.GetManifestResourceNames()){
            if(!AllowedResources.Contains(name)&&!nativeResources.ContainsKey(name)&&name!="licenses.Wintun")throw new InvalidDataException("Unapproved embedded resource: "+name);
            using(var stream=assembly.GetManifestResourceStream(name))using(var memory=new MemoryStream()){
                stream.CopyTo(memory);var data=memory.ToArray();
                if(nativeResources.ContainsKey(name)){if(Hash(data)!=nativeResources[name])throw new InvalidDataException("Modified Wintun resource: "+name);}
                else if(data.Length>=2&&data[0]=='M'&&data[1]=='Z')throw new InvalidDataException("Embedded executable payload: "+name);
                resources.Add(new {Name=name,Bytes=data.Length,SHA256=Hash(data)});
            }
        }
        var imports=new List<object>();
        foreach(var type in assembly.GetTypes())foreach(var method in type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly)){
            if((method.Attributes&MethodAttributes.PinvokeImpl)==0)continue;
            var attributes=CustomAttributeData.GetCustomAttributes(method);
            var import=attributes.FirstOrDefault(a=>a.Constructor.DeclaringType.FullName=="System.Runtime.InteropServices.DllImportAttribute");
            var paths=attributes.FirstOrDefault(a=>a.Constructor.DeclaringType.FullName=="System.Runtime.InteropServices.DefaultDllImportSearchPathsAttribute");
            string dll=import==null?null:import.ConstructorArguments[0].Value as string;
            if(dll==null||!AllowedDlls.Contains(dll,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("Unapproved native library: "+dll);
            if(paths==null||Convert.ToInt32(paths.ConstructorArguments[0].Value)!=2048)throw new InvalidDataException("Native import must restrict search to System32: "+type.FullName+"."+method.Name);
            string entryPoint=import.NamedArguments.Where(a=>a.MemberName=="EntryPoint").Select(a=>a.TypedValue.Value as string).FirstOrDefault()??method.Name;
            if(ReviewRequiredEntryPoints.Contains(entryPoint,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("Native API needs explicit security review: "+entryPoint);
            imports.Add(new {Method=type.FullName+"."+method.Name,Dll=dll,EntryPoint=entryPoint,SearchPath="System32"});
        }
        return new {File=Path.GetFileName(file),Bytes=bytes.Length,SHA256=Hash(bytes),Manifest=Manifest(file),References=references.Select(r=>r.FullName).ToArray(),Resources=resources,NativeImports=imports,PolicyPassed=true,ExecutedApplication=false,Scope="Static composition policy only, not a malware verdict"};
    }
    static int Main(string[] args) {
        try {
            if(args.Length!=2)throw new ArgumentException("Expected EXE and JSON output path");
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve+=(sender,e)=>Assembly.ReflectionOnlyLoad(e.Name);
            object result=Inspect(Path.GetFullPath(args[0]));
            File.WriteAllText(args[1],new JavaScriptSerializer().Serialize(result),new System.Text.UTF8Encoding(false));
            Console.WriteLine("PASS static artifact policy: "+Path.GetFileName(args[0]));return 0;
        }catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
}
