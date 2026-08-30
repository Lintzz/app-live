---
name: testar
description: Compila em Release e abre o RadminStreamApp para o usuário testar na tela, com captura da janela para conferir o resultado. Use quando pedirem para rodar, abrir ou testar o app.
---

# Testar Skill

Abre o app de verdade na tela do usuário, no build mais novo, e confere o resultado por
captura de tela. Não é a suíte de testes — para isso use `dotnet test`.

## Fluxo

### 1. Compilar em Release

```powershell
& ".\.dotnet\dotnet.exe" build RadminStreamLive.sln -c Release -v q --nologo
```

Se falhar, **pare aqui** e mostre o erro. Não adianta abrir um binário velho.

O executável sai em
`src\RadminStreamApp\bin\Release\net8.0-windows10.0.19041.0\RadminStreamApp.exe`.

### 2. Conferir se já tem instância aberta

O `SignalingServer` sobe **junto com o app**, não com a live: duas instâncias brigam pela
porta 8080 e a segunda aparece offline para os amigos.

```powershell
Get-Process RadminStreamApp -ErrorAction SilentlyContinue | Select-Object Id,StartTime
```

Se houver uma rodando, **pergunte antes de encerrar**. Ela pode estar transmitindo para
amigos de verdade — matar o processo derruba a live deles. Para saber se tem gente vendo:

```powershell
@(Get-NetTCPConnection -LocalPort 8080 -State Established -ErrorAction SilentlyContinue).Count
```

### 3. Abrir e trazer para a frente

O terminal costuma cobrir a janela do app; sem o `SetForegroundWindow` a captura sai
mostrando o terminal.

```powershell
Add-Type @'
using System;using System.Runtime.InteropServices;
public class F { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
'@
$exe = "D:\Projetos\radmin-stream-live\src\RadminStreamApp\bin\Release\net8.0-windows10.0.19041.0\RadminStreamApp.exe"
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 5
$p = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
if ($p) { [F]::SetForegroundWindow($p.MainWindowHandle) | Out-Null; "RODANDO PID=$($p.Id) janela='$($p.MainWindowTitle)'" } else { "MORREU antes dos 5s - veja o error.log" }
```

Os 5 segundos não são folga: o WPF ainda não tem `MainWindowHandle` no instante do
`Start-Process`. Se o processo morreu, os logs estão em `%LOCALAPPDATA%\RadminStreamApp\`
(`error.log` e `audio_error.log`).

### 4. Capturar a janela e **olhar** a imagem

Screenshot que você não abriu não vale nada — janela em branco é falha de inicialização.

```powershell
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;using System.Runtime.InteropServices;
public class R { [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out T r);
 public struct T{public int L,Tp,Rr,B;} }
'@
$h = (Get-Process RadminStreamApp).MainWindowHandle
$r = New-Object R+T; [R]::GetWindowRect($h,[ref]$r) | Out-Null
$w = $r.Rr-$r.L; $ht = $r.B-$r.Tp
$b = New-Object System.Drawing.Bitmap($w,$ht); $g = [System.Drawing.Graphics]::FromImage($b)
$g.CopyFromScreen($r.L,$r.Tp,0,0,(New-Object System.Drawing.Size($w,$ht)))
$b.Save("<scratchpad>\app.png",[System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $b.Dispose()
```

Salve no diretório de scratchpad da sessão e leia o PNG com a ferramenta Read.

## Armadilhas desta janela

- **Os controles flutuantes nascem invisíveis.** `SidebarHandle` (botão de amigos),
  `TopPanelHandle` (abinha do painel de transmitir) e `VideoControlsBar` começam com
  `Opacity="0"` e só acendem no `MouseMove` sobre a `ViewerArea`. Para vê-los na captura,
  mova o cursor para dentro da área de vídeo em alguns passos (`SetCursorPos`, ~120 ms entre
  eles), espere ~400 ms pelo fade de 160 ms, e **devolva o cursor à posição original** no
  fim — é o mouse do usuário.
- **A abinha `^` só existe com pelo menos uma live aberta**, e o botão de foco só a partir de
  duas. Sem sessão não adianta procurar por eles na captura.
- **Diferenças de cor aqui são sutis** (`#050508` do vídeo, `#0F0F13` do fundo da janela,
  `#18181E` do painel de cima, `#2D2D35` da borda). Quando o usuário reclamar de "uma
  faixa/barra" que você não enxerga na captura, tire a dúvida amostrando os pixels em vez
  de apertar os olhos:

  ```powershell
  $prev=""; for($y=110;$y -lt 150;$y++){ $c=$b.GetPixel(600,$y)
    $hex="{0:X2}{1:X2}{2:X2}" -f $c.R,$c.G,$c.B; if($hex -ne $prev){ "y=$y #$hex"; $prev=$hex } }
  ```

- **Não rode `dotnet test` com o app aberto.** O projeto de testes referencia o app e o
  rebuild trava no `RadminStreamApp.dll` em uso.
- **O usuário está mexendo na mesma janela.** Se um clique seu não surtir efeito, considere
  que ele minimizou, fechou ou clicou em outra coisa antes de concluir que há bug —
  `GetWindowRect` devolvendo coordenadas perto de `-32000` significa janela minimizada.

## Ao terminar

Deixe o app aberto (o pedido é para o usuário testar) e diga em uma lista curta o que ele
deve conferir na tela, ligando cada item à mudança que motivou a rodada.
