using System; using System.IO; using System.Linq; using System.Reflection;
class P {
 static void Main(){
  var dir=@"C:\Program Files (x86)\Steam\steamapps\common\Outer Wilds\OuterWilds_Data\Managed";
  AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var n=new AssemblyName(a.Name).Name+".dll"; var c=Path.Combine(dir,n); return File.Exists(c)?Assembly.LoadFrom(c):null;};
  var asm=Assembly.LoadFrom(Path.Combine(dir,"Assembly-CSharp.dll"));
  Type[] types; try{types=asm.GetTypes();}catch(ReflectionTypeLoadException ex){types=ex.Types.Where(t=>t!=null).ToArray();}
  var t=types.First(x=>x.Name=="OWRigidbody");
  foreach(var m in t.GetMethods(BindingFlags.Public|BindingFlags.Instance).Where(m=>m.Name.Contains("Position")||m.Name.Contains("GetWorld")||m.Name=="get_position").OrderBy(m=>m.Name))
    Console.WriteLine(m.ReturnType.Name+" "+m.Name);
 }
}
