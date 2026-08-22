# app-live

# Radmin Stream

Radmin Stream é um aplicativo desktop desenvolvido em C# com WPF (.NET 8.0) para captura e transmissão de áudio e vídeo em tempo real. Ele inclui suporte avançado para captura de tela (vídeo) e recursos de captura de áudio tanto globais (system-wide) quanto por processo, utilizando WebSockets e WebRTC para comunicação e streaming.

historia do usuário
O que quero um app que eu instale no meu pc e mande para meus amigos instalarem no pc deles onde tem duas opções host e join, onde quem faz o host tem a opção de stremar os monitores que tiverem no meu caso duas telas, essas transmissão não pode captar audio do discord porque sempre fico em call e não quero que as pessoas se ovem, as pessoas tem que escutar os sons perfeitamente e uma qualidade 1080p, usando radminvpn para se conectar, tem que ter modo teatro que some tudo so fica a live e a pessoa consegue deixar a tela do tamanho que quiser e fullscreen que deixa do tamanho completo, quem tiver assistindo tem o controle do volume, quem esta host apenas ve oque esta transmitindo so que sem som, a opção de audio e apenas para quem estiver assistido

## Pré-requisitos

Para compilar e gerar o instalador do projeto, você precisará instalar as seguintes ferramentas:

1. **.NET SDK 8.0**: Necessário para construir a aplicação.
   - [Download .NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
2. **Inno Setup 6**: Necessário para gerar o arquivo `.exe` de instalação (`setup.exe`).
   - Pode ser instalado via WinGet: `winget install JRSoftware.InnoSetup`
   - Ou baixado diretamente em: [jrsoftware.org](https://jrsoftware.org/isdl.php)

## Como Compilar o Projeto

### 1. Construir e Publicar a Aplicação

Para gerar os arquivos binários da aplicação, abra o terminal na pasta do projeto e execute o seguinte comando:

```bash
dotnet publish RadminStreamApp.csproj -c Release -r win-x64 --self-contained true -o "publish_zip"
```

Isso irá compilar a aplicação em modo `Release` (incluindo todo o runtime do .NET para que o usuário final não precise instalá-lo) e colocar todos os arquivos necessários dentro da pasta `publish_zip`.

### 2. Gerar o Instalador (`setup.exe`)

Depois que os arquivos estiverem na pasta `publish_zip`, você usará o Inno Setup para empacotar a aplicação em um executável instalável.

#### Opção A: Usando a Interface Gráfica do Inno Setup

1. Abra o arquivo `setup.iss` no **Inno Setup Compiler**.
2. Clique no botão **Compile** (ou pressione `Ctrl+F9`).
3. O arquivo `RadminStream_Setup.exe` será gerado na raiz do projeto.

#### Opção B: Via Linha de Comando (Prompt/PowerShell)

Execute o compilador do Inno Setup (`ISCC.exe`) apontando para o seu script de setup. O caminho pode variar dependendo de como o Inno Setup foi instalado:

```powershell
# Exemplo de caminho padrão se instalado no Program Files:
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" setup.iss

# Se foi instalado em AppData (ex: por outras ferramentas):
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" setup.iss
```

O instalador será gerado na mesma pasta com o nome **`RadminStream_Setup.exe`**.

## Estrutura do Projeto

- **`RadminStreamApp.csproj`**: Arquivo principal do projeto C# WPF.
- **`setup.iss`**: Script de configuração do Inno Setup utilizado para gerar o instalador final da aplicação.
- **`publish_zip/`**: Diretório temporário gerado durante o processo de `dotnet publish`.
- **Bibliotecas**: O projeto faz uso de pacotes importantes como `Fleck` (WebSocket), `NAudio` (Áudio), e `SIPSorcery` (WebRTC). A DLL `ApplicationLoopback.dll` também é preservada durante a publicação para fornecer suporte à captura de áudio específica por processo.

## Contribuindo / Solução de Problemas

Se encontrar erros de compilação relacionados à versão do .NET, certifique-se de que o SDK correto está instalado e configurado no PATH do seu sistema.
Erros de "processo ausente" ou problemas de áudio podem ocorrer se a API `ApplicationLoopback` não for suportada por sua versão do Windows (recomendado Windows 10 Build 20348+ ou Windows 11).
