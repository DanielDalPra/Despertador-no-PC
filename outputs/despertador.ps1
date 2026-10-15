param([switch]$Test, [ValidateRange(1, 60)][int]$TestMinutes = 5)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AwakeAlarm {
  [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
  public static Point SavedPoint;
  public static IntPtr SavedWindow;
  public static bool SaveClickTarget() {
    if (!GetCursorPos(out SavedPoint)) return false;
    SavedWindow = WindowFromPoint(SavedPoint);
    return SavedWindow != IntPtr.Zero;
  }
  public static bool ClickSavedTarget() {
    if (SavedWindow == IntPtr.Zero || WindowFromPoint(SavedPoint) != SavedWindow) return false;
    if (!SetCursorPos(SavedPoint.X, SavedPoint.Y)) return false;
    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    return true;
  }
  [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
  [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
  public static void MaxVolume() {
    for (int i = 0; i < 100; i++) {
      keybd_event(0xAF, 0, 0, UIntPtr.Zero);
      keybd_event(0xAF, 0, 2, UIntPtr.Zero);
    }
  }
}
'@

$alarmDate = [datetime]::ParseExact('2026-10-14', 'yyyy-MM-dd', [cultureinfo]::InvariantCulture)
$times = @('06:00', '06:30', '07:00')
$alarms = @($times | ForEach-Object { $alarmDate.Add([timespan]::Parse($_)) } | Where-Object { $_ -gt (Get-Date) })
if ($Test) { $alarms = @((Get-Date).AddMinutes($TestMinutes)) }
if ($alarms.Count -eq 0) {
    Write-Host 'Todos os horarios de 14/10/2026 ja passaram.'
    Read-Host 'Pressione Enter para sair'
    exit
}

# Generate a local WAV; no internet connection is needed.
$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
$rate = 22050
$samples = $rate * 2
$writer.Write([System.Text.Encoding]::ASCII.GetBytes('RIFF'))
$writer.Write([int](36 + $samples * 2))
$writer.Write([System.Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
$writer.Write([int]16)
$writer.Write([int16]1)
$writer.Write([int16]1)
$writer.Write([int]$rate)
$writer.Write([int]($rate * 2))
$writer.Write([int16]2)
$writer.Write([int16]16)
$writer.Write([System.Text.Encoding]::ASCII.GetBytes('data'))
$writer.Write([int]($samples * 2))
$oscillatorPhase = 0.0
$bassPhase = 0.0
for ($i = 0; $i -lt $samples; $i++) {
    $t = $i / [double]$rate
    # Mix the siren with a 110 Hz bass tone within the 16-bit PCM limit.
    $frequency = if (($t % 0.5) -lt 0.25) { 880 } else { 1320 }
    $oscillatorPhase = ($oscillatorPhase + $frequency / [double]$rate) % 1.0
    $bassPhase = ($bassPhase + 110 / [double]$rate) % 1.0
    $siren = if ($oscillatorPhase -lt 0.5) { 1.0 } else { -1.0 }
    $bass = if ($bassPhase -lt 0.5) { 1.0 } else { -1.0 }
    $value = [int16][Math]::Round(32767 * (0.70 * $siren + 0.30 * $bass))
    $writer.Write([int16]$value)
}
$writer.Flush()
$stream.Position = 0
$player = New-Object System.Media.SoundPlayer
$player.Stream = $stream
$player.Load()

try {
    [void][AwakeAlarm]::SetThreadExecutionState([uint32]2147483649)
    if ($Test) {
        $Host.UI.RawUI.WindowTitle = 'TESTE DO DESPERTADOR - digite ACORDEI quando tocar'
        Write-Host ('TESTE AGENDADO PARA ' + $alarms[0].ToString('dd/MM HH:mm:ss')) -ForegroundColor Cyan
    } else {
        Write-Host 'DESPERTADOR - 14/10/2026' -ForegroundColor Cyan
    }
    Write-Host ('Horarios pendentes: ' + (($alarms | ForEach-Object { $_.ToString('HH:mm') }) -join ', '))
    Write-Host ''
    Write-Host 'Mantenha esta janela aberta, o computador ligado e conectado a tomada.'
    Write-Host 'A tela pode apagar. Nao desligue, hiberne ou feche a tampa do notebook.'
    Write-Host 'Confira a saida de audio e o volume. Fone Bluetooth pode desligar sozinho.'
    Write-Host ''
    Write-Host 'ATENCAO: o volume do Windows sera elevado a 100%. Tire o fone dos ouvidos.' -ForegroundColor Yellow
    if (-not $Test) {
    Read-Host 'O teste de som dura 3 segundos. Pressione Enter para testar no maximo'
    [AwakeAlarm]::MaxVolume()
    $player.PlayLooping()
    Start-Sleep -Seconds 3
    $player.Stop()
    $answer = Read-Host 'Voce ouviu o teste no volume desejado? Digite S para ativar'
    if ($answer -notmatch '^[sS]$') { exit }
    }
    $clickPending = $false
    $clickAnswer = Read-Host 'Quer UM clique no primeiro alarme para ligar o efeito JBL? Digite S, ou Enter para nao'
    if ($clickAnswer -match '^[sS]$') {
        Write-Host 'Abra a tela de iluminacao da JBL. Deixe o efeito desligado e posicione o mouse sobre Falha.'
        Write-Host 'Voce tem 10 segundos. Nao clique. A posicao sera salva automaticamente.' -ForegroundColor Yellow
        Start-Sleep -Seconds 10
        $clickPending = [AwakeAlarm]::SaveClickTarget()
        if ($clickPending) {
            Write-Host 'Posicao salva. Deixe o app JBL visivel e o Windows desbloqueado.' -ForegroundColor Green
        } else {
            Write-Host 'Nao foi possivel salvar a posicao. O alarme de som continua ativo.'
        }
    }
    Write-Host 'ATIVADO. Cada alarme toca por ate 5 minutos.' -ForegroundColor Green
    Write-Host 'Durante o alarme: digite ACORDEI e pressione Enter para parar esse toque.'
    Write-Host 'Enquanto espera: Q encerra o programa.'
    Write-Host 'Para testar novamente enquanto espera, pressione T nesta janela.'

    foreach ($alarm in $alarms) {
        Write-Host ('Proximo alarme: ' + $alarm.ToString('dd/MM HH:mm'))
        while ((Get-Date) -lt $alarm) {
            if ([Console]::KeyAvailable) {
                $key = [Console]::ReadKey($true).Key
                if ($key -eq 'Q') { return }
                if ($key -eq 'T') {
                    [AwakeAlarm]::MaxVolume()
                    $player.PlayLooping()
                    Start-Sleep -Seconds 3
                    $player.Stop()
                }
            }
            Start-Sleep -Milliseconds 250
        }
        # Discard keys pressed before this alarm started.
        while ([Console]::KeyAvailable) { [void][Console]::ReadKey($true) }
        Write-Host ('ALARME ' + $alarm.ToString('HH:mm') + ' - digite ACORDEI e pressione Enter') -ForegroundColor Yellow
        if ($clickPending) {
            if ([AwakeAlarm]::ClickSavedTarget()) {
                Write-Host 'Clique enviado ao ponto salvo. Para digitar ACORDEI, volte a esta janela.'
            } else {
                Write-Host 'Clique ignorado: o alvo foi coberto ou mudou. O som vai tocar normalmente.'
            }
            $clickPending = $false
        }
        [AwakeAlarm]::MaxVolume()
        $player.PlayLooping()
        $end = (Get-Date).AddMinutes(5)
        $typed = ''
        while ((Get-Date) -lt $end) {
            if ([Console]::KeyAvailable) {
                $inputKey = [Console]::ReadKey($true)
                if ($inputKey.Key -eq 'Enter') {
                    Write-Host ''
                    if ($typed.Trim() -ieq 'ACORDEI') { break }
                    Write-Host 'Digite ACORDEI e pressione Enter para parar.'
                    $typed = ''
                } elseif ($inputKey.Key -eq 'Backspace') {
                    if ($typed.Length -gt 0) {
                        $typed = $typed.Substring(0, $typed.Length - 1)
                        [Console]::Write("`b `b")
                    }
                } elseif (-not [char]::IsControl($inputKey.KeyChar) -and $typed.Length -lt 80) {
                    $typed += $inputKey.KeyChar
                    [Console]::Write($inputKey.KeyChar)
                }
            }
            Start-Sleep -Milliseconds 100
        }
        $player.Stop()
        Write-Host ''
    }
    Write-Host 'Todos os alarmes foram concluidos.'
    Read-Host 'Pressione Enter para sair'
} finally {
    $player.Stop()
    $player.Dispose()
    $writer.Dispose()
    $stream.Dispose()
    [void][AwakeAlarm]::SetThreadExecutionState([uint32]2147483648)
}


