# 🎥 Radmin Stream

![Status](https://img.shields.io/badge/Status-Em%20Desenvolvimento-yellow)
![Plataforma](https://img.shields.io/badge/Plataforma-Windows-blue)
![Framework](https://img.shields.io/badge/.NET-8.0-purple)

**Radmin Stream** é um aplicativo desktop desenvolvido em C# com WPF (.NET 8.0) focado na captura e transmissão de áudio e vídeo em tempo real. Ele foi projetado para facilitar sessões de streaming privadas com **amigos** via [Radmin VPN](https://www.radmin-vpn.com/), oferecendo opções avançadas de captura de tela e áudio (global ou por processo) usando WebSockets e WebRTC.

> ⚠️ **Aviso de Segurança Importante!**
> Este aplicativo foi feito **exclusivamente para ser usado entre amigos de confiança**. 
> **Não o utilize com estranhos ou pessoas não confiáveis.** O projeto provavelmente possui vulnerabilidades de segurança, não foi auditado profissionalmente e não conta com proteções robustas contra usuários mal-intencionados na rede.

---

## 📖 História do Usuário (Objetivo)

O projeto nasceu da seguinte necessidade:
*Um app instalável para hostear transmissões ou participar (Join) de lives com amigos via Radmin VPN.*

* **Para quem transmite (Host):**
  * Escolher qual monitor transmitir (suporte a múltiplas telas).
  * Isolar o áudio de programas específicos (ex: não captar o áudio do Discord, para evitar retorno nas chamadas).
  * O Host não ouve a própria transmissão.
* **Para quem assiste (Join):**
  * Receber áudio e vídeo em alta qualidade (1080p).
  * Modo teatro (somente a live) e Tela Cheia (Fullscreen).
  * Controle de volume local.

---

## 🛠️ Pré-requisitos

Para compilar e gerar o instalador do projeto, você precisará das seguintes ferramentas:

1. **.NET SDK 8.0**: Necessário para construir a aplicação (no projeto, utilizamos um binário local na pasta `.dotnet`).
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
- **Bibliotecas Importantes**: 
  - `Fleck` (WebSocket)
  - `NAudio` (Áudio)
  - `SIPSorcery` (WebRTC)
  - A DLL `ApplicationLoopback.dll` é preservada para fornecer suporte à captura de áudio específica por processo.

---

## 🐛 Solução de Problemas

- **Erros de .NET:** Certifique-se de que o executável `.\.dotnet\dotnet.exe` está presente e funcional no diretório do projeto.
- **Processo Ausente / Sem Áudio:** Pode ocorrer se a API `ApplicationLoopback` não for suportada no seu Windows (é recomendado usar Windows 10 Build 20348+ ou Windows 11).
