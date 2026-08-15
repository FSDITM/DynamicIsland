# Диагностика: кому Windows отдаёт клик в разных точках экрана.
# Запускать при воспроизведении проблемы, НЕ на заблокированном экране.
#
#   powershell -ExecutionPolicy Bypass -File probe.ps1
#
# В строках с DynamicIsland.Overlay клик забирает островок — там и проблема.

Add-Type @"
using System;using System.Text;using System.Runtime.InteropServices;
public class Probe {
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(PT p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowRgnBox(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetExStyle(IntPtr h,int i);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);

  public static string Cls(IntPtr h){ var sb=new StringBuilder(256); GetClassNameW(h,sb,256); return sb.ToString(); }

  public static string Who(int x,int y){
    var p=new PT{X=x,Y=y}; var h=WindowFromPoint(p);
    if(h==IntPtr.Zero) return "нет окна";
    uint pid; GetWindowThreadProcessId(h,out pid);
    string proc; try { proc=System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { proc="?"; }
    return string.Format("{0,-26} [{1}]", Cls(h), proc);
  }

  public static IntPtr FindIsland(){
    IntPtr f=IntPtr.Zero;
    EnumWindows((h,p)=>{ if(Cls(h)=="DynamicIsland.Overlay"){ f=h; return false; } return true; },IntPtr.Zero);
    return f;
  }
}
"@

$screenW = [System.Windows.Forms.SystemInformation]::VirtualScreen.Width
if (-not $screenW) { Add-Type -AssemblyName System.Windows.Forms; $screenW = [System.Windows.Forms.SystemInformation]::VirtualScreen.Width }
$cx = [int]($screenW / 2)

Write-Host "=== окно островка ==="
$h = [Probe]::FindIsland()
if ($h -eq [IntPtr]::Zero) {
    Write-Host "  островок не запущен"
} else {
    $wr = New-Object Probe+RECT; [void][Probe]::GetWindowRect($h, [ref]$wr)
    $rb = New-Object Probe+RECT; $t = [Probe]::GetWindowRgnBox($h, [ref]$rb)
    $ex = [Probe]::GetExStyle($h, -20).ToInt64()
    Write-Host ("  окно   : {0},{1} .. {2},{3}  ({4}x{5})" -f $wr.L,$wr.T,$wr.R,$wr.B,($wr.R-$wr.L),($wr.B-$wr.T))
    if ($t -ge 2) {
        Write-Host ("  регион : {0},{1} .. {2},{3}  ({4}x{5})" -f $rb.L,$rb.T,$rb.R,$rb.B,($rb.R-$rb.L),($rb.B-$rb.T))
    } else {
        Write-Host "  регион : НЕ ЗАДАН — окно занимает всю свою площадь"
    }
    Write-Host ("  WS_EX_TRANSPARENT: {0}" -f (($ex -band 0x20) -ne 0))
}

Write-Host ""
Write-Host "=== кому достанется клик (центр экрана по вертикали вниз) ==="
foreach ($y in 2,10,25,50,80,120,170,230,300,380) {
    Write-Host ("  ({0,4},{1,4}) -> {2}" -f $cx,$y,([Probe]::Who($cx,$y)))
}

Write-Host ""
Write-Host "=== по горизонтали на высоте 60 ==="
foreach ($dx in -600,-400,-250,-100,0,100,250,400,600) {
    $x = $cx + $dx
    Write-Host ("  ({0,4},  60) -> {1}" -f $x,([Probe]::Who($x,60)))
}
