# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Idioma

O repositório inteiro é escrito em português — README, comentários de código, mensagens de
commit e textos de UI. Mantenha esse padrão ao escrever código novo.

## Comandos

O SDK fica em `.dotnet/` (fora do versionamento). Num clone novo essa pasta não existe: use
`dotnet` global (o `global.json` já fixa a linha 8.0) ou recrie a pasta com o
`dotnet-install.ps1 -Channel 8.0 -InstallDir .dotnet`.

```powershell
# Compilar
.\.dotnet\dotnet.exe build StreamLive.sln -c Release

# Rodar todos os testes
.\.dotnet\dotnet.exe test StreamLive.sln -c Release

# Rodar uma classe / um teste
.\.dotnet\dotnet.exe test StreamLive.sln --filter "FullyQualifiedName~DuplicationRecoveryTests"
.\.dotnet\dotnet.exe test StreamLive.sln --filter "FullyQualifiedName~SignalingHandshakeTests.CorrectPasswordIsAccepted"

# Publicar + gerar o instalador (skill /build-installer)
if (Test-Path "publish_zip") { Remove-Item -Recurse -Force "publish_zip" } ; & ".\.dotnet\dotnet.exe" publish src\StreamLiveApp\StreamLiveApp.csproj -c Release -r win-x64 --self-contained true -o "publish_zip" ; & "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" build\setup.iss
```

**Git LFS é obrigatório.** As DLLs do FFmpeg em `src/StreamLiveApp/FFmpegLibs/` (~145 MB)
vivem no LFS. Sem `git lfs`, o working tree recebe ponteiros de texto e o build falha ao
carregar o FFmpeg (`git lfs pull` conserta um clone já feito).

**Versão em um lugar só:** `<Version>` no `StreamLiveApp.csproj`. Dali saem o
`AssemblyVersion`, o `AppInfo.Version` mostrado na UI e o `build/version.iss` (gerado pelo
target `GenerateInnoSetupVersion`, consumido pelo `setup.iss`). Nunca edite `version.iss` nem
repita a versão no XAML. O fluxo completo de release está na skill `/publish-release` — inclui
publicar o `.sha256` junto do instalador, sem o qual o auto-update recusa a atualização.

O CI (`.github/workflows/ci.yml`) roda restore + build + test em `windows-latest`.

## Arquitetura

App WPF (.NET 8, `net8.0-windows10.0.19041.0`) que transmite tela e áudio entre amigos numa
Radmin VPN. A mesma janela é host e viewer ao mesmo tempo.

### Dois caminhos, um `StreamManager`

`StreamManager` (Core) é a peça central e serve os dois papéis — no host cria capturadores e
encoder, no viewer só decodifica. `EnsureCapturers()` só roda no host; `_isHost` separa o resto.

- **Host:** `MainWindow` → `HostBroadcast` (ciclo de vida da live, zero UI) → `StreamManager`
  → `VideoCapturer`/`AudioCapturer`. O `SignalingServer` (Fleck, porta 8080) sobe **junto com o
  app**, não com a live: é ele que responde ao `STATUS_CHECK` dos amigos e faz você aparecer
  como online na lista deles.
- **Viewer:** `MainWindow` → uma `ViewerSession` por live aberta (várias em grade) →
  `SignalingClient` (Websocket.Client) + `StreamManager`. `ViewerSession` é `INotifyPropertyChanged`
  e a UI faz binding nela; o code-behind do `MainWindow` cuida só de foco, grade, PiP e teatro.

### Transportes (assimétricos de propósito)

| Mídia | Codec | Caminho |
|---|---|---|
| Vídeo | H.264 (FFmpeg, `ultrafast`+`zerolatency`) | trilha de vídeo do WebRTC (SIPSorcery) |
| Áudio | PCM 44,1 kHz estéreo 16 bits, **sem compressão** | quadro binário no WebSocket de sinalização, prefixado pelo byte `1` |
| Sinalização | JSON (`SignalingMessage`) | WebSocket `ws://` porta 8080 |

O áudio já viajou em Opus pela trilha do WebRTC (v1.0.18–v1.0.21) e foi **revertido** por nunca
ter funcionado em campo. Custa ~1,4 Mbps por viewer e não sincroniza com o vídeo; a deriva de
relógio é contida pelo `LatencyTrimmingProvider` (teto 250 ms, alvo 80 ms). Não reintroduza o
Opus sem medir ponta a ponta.

### Handshake de sala (`SignalingServer` ↔ `ViewerSession`)

`STATUS_CHECK`/`STATUS_RESPONSE` · `AUTH_REQUIRED`(desafio) → `AUTH`(HMAC) → `AUTH_OK`/`AUTH_FAIL`
· `CLIENT_CONNECTED` · `offer`/`answer`/`ice` · `STREAM_STARTED`/`STREAM_STOPPED`/`SOURCE_CHANGED`.

Detalhes que quebram fácil:
- O host responde `AUTH_REQUIRED` a **cada** mensagem pré-autenticação (o `CLIENT_CONNECTED` e
  um por candidato ICE). `ViewerSession._passwordPromptOpen` impede que isso abra quatro modais.
- Só quem manda `CLIENT_CONNECTED` entra em `_viewers`; conexões de `STATUS_CHECK` ficam fora do
  broadcast, senão recebem áudio binário no lugar do `STATUS_RESPONSE`.
- `SignalingServer.NormalizeIp` existe porque o Fleck entrega IPv4 mapeado (`::ffff:x.x.x.x`) e
  `::1`; sem normalizar, nada casa com a lista de amigos. `127.0.0.1` sempre passa.
- A senha nunca trafega: `CryptoHelper.ComputeAuthProof` devolve o HMAC do desafio com a chave
  PBKDF2 (200k iterações, salt fixo da aplicação, cache obrigatório — derivar custa ~100 ms e o
  áudio chamaria isso ~50×/s). Payload da sala é AES-GCM, formato `[nonce 12][tag 16][cipher]`.

### Captura de tela com fallback

`VideoCapturer` tenta `DesktopDuplicationGrabber` (DXGI/Vortice) e cai para `CopyFromScreen`
(GDI) quando a duplicação não existe (RDP, driver antigo) ou nunca entregou quadro. A duplicação
**morre** em situações comuns (tela cheia exclusiva, UAC, troca de modo de vídeo) e não volta
sozinha — `DecideDuplicationAction` decide entre usar / esperar / recriar / desistir para o GDI.
O quadro sai em **BGRA cru**; a conversão de cor é do swscale, e `VideoEncoderFormatTests` trava
essa decisão contra upgrades do SIPSorcery.

### Áudio: exclusão por processo

`AudioCapturer` usa `WasapiLoopbackCapture` para o sistema inteiro, ou `ProcessAudioCapturer`
(P/Invoke em `NativeLibs/ApplicationLoopback.dll`, Windows 10 20348+) para **excluir** um
processo. O padrão é excluir o Discord: sem isso a mesa se escuta em eco. A escolha é persistida
por **nome** de processo (`AudioExclusionService` resolve o PID a cada live, porque o programa
pode ter sido reaberto). `SetTargetProcess` reabre a captura fora da thread de UI — os
parâmetros só chegam ao Windows na abertura.

### Persistência e estado

`friends.json` e `settings.json` em `%LOCALAPPDATA%\StreamLiveApp\` (mesma pasta de
`error.log` e `audio_error.log`). O caminho sai **só** do `AppPaths` — ele cria a pasta e, na
primeira execução depois do rename do app, move a pasta da versão anterior por cima da nova
(sem isso o usuário abriria o app com a lista de amigos vazia). Montar o caminho à mão em
outro lugar reintroduz o bug: quem criasse a pasta nova primeiro cancelaria a migração.
`SettingsService.Save` grava em `.tmp` e move por cima.
`UpdateManager` consulta a release mais recente do GitHub (`AppInfo.RepositoryOwner/Name`) e
**exige** o `.sha256` publicado ao lado do instalador.

## Convenções

- Comentários explicam **por quê**, não o quê — quase todo bloco de comentário no código
  documenta um bug real e a decisão que o fechou. Ao mexer numa dessas áreas, preserve ou
  atualize a explicação em vez de apagá-la.
- `Nullable` e `ImplicitUsings` ligados nos dois projetos.
- Lógica testável é exposta como `internal static` puro (`DecideDuplicationAction`,
  `ShouldReemit`, `CopyRect`, `NormalizeIp`) e alcançada pelo `InternalsVisibleTo` para
  `StreamLiveApp.Tests`. Prefira esse formato a testar através da UI ou da captura real.
- Testes são xUnit em `tests/StreamLiveApp.Tests/`; o `SignalingHandshakeTests` sobe um
  `SignalingServer` real em porta livre. O `NoWarn NU1903` (advisories do SIPSorcery 8.0.23) é
  proposital — só sai ao migrar para .NET 10.
- A seção "Estrutura do Projeto" do README ainda afirma que não há suíte de testes; está
  desatualizada — `tests/` existe e o CI a executa.
