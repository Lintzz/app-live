# 🎥 Radmin Stream

![Status](https://img.shields.io/badge/Status-Em%20Desenvolvimento-yellow)
![Plataforma](https://img.shields.io/badge/Plataforma-Windows-blue)
![Framework](https://img.shields.io/badge/.NET-8.0-purple)

**Radmin Stream** é um aplicativo desktop desenvolvido em C# com WPF (.NET 8.0) focado na captura e transmissão de áudio e vídeo em tempo real. Ele foi projetado para facilitar sessões de streaming privadas com **amigos** via [Radmin VPN](https://www.radmin-vpn.com/), oferecendo opções avançadas de captura de tela e áudio (global ou por processo) usando WebSockets e WebRTC.

> ⚠️ **Aviso de Segurança Importante!**
> Este aplicativo foi feito **exclusivamente para ser usado entre amigos de confiança**.
> **Não o utilize com estranhos ou pessoas não confiáveis.** O projeto não foi auditado profissionalmente.
>
> O que existe hoje de proteção:
> * **Lista de permissão por IP** (ligada por padrão): só quem está na sua lista de amigos consegue abrir conexão. Pode ser desligada em *Configurações*.
> * **Senha de sala opcional** com autenticação por desafio-resposta — a senha nunca trafega na rede; o viewer devolve um HMAC do desafio.
> * **Criptografia AES-GCM** do conteúdo da sala, com chave derivada por PBKDF2 (200k iterações).
>
> O que **não** existe: TLS no canal de sinalização, certificados, ou qualquer defesa contra alguém que já tenha acesso privilegiado à sua rede virtual.

---

## 📖 História do Usuário (Objetivo)

O projeto nasceu da seguinte necessidade:
*Um app instalável para hostear transmissões ou participar (Join) de lives com amigos via Radmin VPN.*

* **Para quem transmite (Host):**
  * Escolher qual monitor transmitir (suporte a múltiplas telas).
  * Isolar o áudio de um programa específico — em *Configurações → Excluir o áudio de um programa*
    você escolhe qual app fica fora da captura (ex.: o Discord, para evitar retorno nas chamadas).
    Se o programa escolhido não estiver aberto, o app avisa que todo o áudio do sistema será transmitido.
  * O Host não ouve a própria transmissão.
* **Para quem assiste (Join):**
  * Receber áudio e vídeo em alta qualidade (1080p).
  * Modo teatro (somente a live) e Tela Cheia (Fullscreen).
  * Controle de volume local.

---

## 🛠️ Pré-requisitos

Para compilar e gerar o instalador do projeto, você precisará das seguintes ferramentas:

1. **.NET SDK 8.0**: Necessário para construir a aplicação. Os comandos abaixo usam um SDK
   local em `.dotnet` (fora do versionamento). Num clone novo essa pasta não existe: instale o
   SDK normalmente (`winget install Microsoft.DotNet.SDK.8`) e troque `.\.dotnet\dotnet.exe`
   por `dotnet`, ou recrie a pasta com o
   [script oficial da Microsoft](https://dot.net/v1/dotnet-install.ps1):
   `.\dotnet-install.ps1 -Channel 8.0 -InstallDir .dotnet`
2. **Inno Setup 6**: Necessário para gerar o arquivo `.exe` de instalação (`setup.exe`).
   - Pode ser instalado via WinGet: `winget install JRSoftware.InnoSetup`
   - Ou baixado diretamente em: [jrsoftware.org](https://jrsoftware.org/isdl.php)

---

## 🚀 Como Compilar o Projeto

O processo de compilação foi automatizado para rodar em uma única linha de comando via PowerShell. O comando limpa os diretórios de builds anteriores, publica o executável em modo *self-contained* e já aciona o Inno Setup para gerar o instalador final.

Abra o terminal **PowerShell** na pasta raiz do projeto e execute:

```powershell
if (Test-Path "publish_zip") { Remove-Item -Recurse -Force "publish_zip" } ; & ".\.dotnet\dotnet.exe" publish RadminStreamApp.csproj -c Release -r win-x64 --self-contained true -o "publish_zip" ; & "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" setup.iss
```

> 💡 **Dica:** Caso o `ISCC.exe` não seja encontrado no diretório local durante a compilação, verifique se o Inno Setup foi instalado globalmente em `C:\Program Files (x86)\Inno Setup 6\ISCC.exe` e ajuste o caminho no comando acima.

O instalador final será gerado na raiz do projeto com o nome **`RadminStream_Setup.exe`**.

---

## 📁 Estrutura do Projeto

- `RadminStreamApp.csproj`: Arquivo principal do projeto C# WPF.
- `setup.iss`: Script de configuração do Inno Setup utilizado para gerar o instalador final.
- `publish_zip/`: Diretório temporário gerado durante a publicação (criado e apagado automaticamente).
- `tests/RadminStreamApp.Tests/`: suíte de testes (xUnit). Rode com
  `.\.dotnet\dotnet.exe test tests\RadminStreamApp.Tests\RadminStreamApp.Tests.csproj`.
- `docs/`: documentos de planejamento.
- `version.iss`: gerado pelo build a partir de `<Version>` no `.csproj` — **não editar à mão**.
- **Bibliotecas Importantes**:
  - `Fleck` (WebSocket)
  - `NAudio` (Áudio)
  - `SIPSorcery` (WebRTC — vídeo H.264)
  - `Vortice.Direct3D11` / `Vortice.DXGI` (captura de tela via Desktop Duplication)
  - A DLL `ApplicationLoopback.dll` é preservada para fornecer suporte à captura de áudio específica por processo.

---

## 🎛️ Como a mídia trafega

| Mídia | Codec | Transporte |
|---|---|---|
| Vídeo | H.264 (libx264, `ultrafast` + `zerolatency`) | Trilha de vídeo do WebRTC |
| Áudio | PCM 44,1 kHz estéreo, 16 bits (sem compressão) | WebSocket de sinalização |
| Sinalização | JSON | WebSocket na porta 8080 |

A captura de tela usa a **Desktop Duplication API (DXGI)**, com o `CopyFromScreen` do GDI como
reserva automática para máquinas onde a duplicação não está disponível (RDP, drivers antigos).

> O áudio já viajou em Opus pela trilha do WebRTC (v1.0.18 a v1.0.21), o que gastaria bem menos
> banda e manteria som e imagem em sincronia. Nunca funcionou em campo e foi revertido. O PCM
> cru custa ~1,4 Mbps por viewer e não sincroniza com o vídeo, mas funciona — e o atraso que
> se acumula por diferença de relógio é contido pelo `LatencyTrimmingProvider`.

---

## ⚖️ Limitações conhecidas

- **`SIPSorcery` 8.0.23 tem advisories abertos** (`GHSA-28gm-jrmw-xx93`, `GHSA-jwjp-4649-v8jp`).
  A correção só existe na linha 10.x, que exige **.NET 10** — migrar o projeto inteiro é o
  pré-requisito para fechar esse ponto.
- O canal de sinalização é `ws://` puro, sem TLS.

---

## 📦 Peso do repositório

As DLLs do FFmpeg em `FFmpegLibs/` somam ~145 MB e estão no histórico do Git. O `.gitattributes`
já as marca como LFS, o que evita que **futuras** atualizações delas engordem o histórico.

Para remover os blobs já commitados é preciso **reescrever o histórico** — operação destrutiva,
que invalida todos os clones existentes. Se for o caso, com todos avisados:

```powershell
git lfs migrate import --include="FFmpegLibs/*.dll" --everything
git push --force-with-lease --all
```

---

## 🐛 Solução de Problemas

- **Erros de .NET:** Certifique-se de que o executável `.\.dotnet\dotnet.exe` está presente e funcional no diretório do projeto.
- **Processo Ausente / Sem Áudio:** Pode ocorrer se a API `ApplicationLoopback` não for suportada no seu Windows (é recomendado usar Windows 10 Build 20348+ ou Windows 11).
