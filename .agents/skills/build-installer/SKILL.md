---
name: build-installer
description: Compila a aplicação RadminStreamApp (modo Release, self-contained) e gera o instalador RadminStream_Setup.exe usando o Inno Setup.
---

# Build Installer Skill

Esta skill automatiza o processo de compilação do `RadminStreamApp` e geração do executável de instalação (`setup.exe`).

## Pré-requisitos
- O SDK do .NET 8.0 deve estar disponível (`.dotnet/dotnet.exe`).
- O Inno Setup 6 deve estar instalado (normalmente em `$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe` ou `C:\Program Files (x86)\Inno Setup 6\ISCC.exe`).

## Comandos a serem executados
Para acionar a construção, sempre execute o seguinte comando PowerShell na raiz do projeto:

```powershell
if (Test-Path "publish_zip") { Remove-Item -Recurse -Force "publish_zip" } ; & ".\.dotnet\dotnet.exe" publish RadminStreamApp.csproj -c Release -r win-x64 --self-contained true -o "publish_zip" ; & "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" setup.iss
```

Caso o `ISCC.exe` não seja encontrado no diretório local, procure em `C:\Program Files (x86)\Inno Setup 6\ISCC.exe`.

O artefato gerado será colocado na raiz do projeto com o nome **`RadminStream_Setup.exe`**.
