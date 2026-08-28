---
name: publish-release
description: Automatiza o processo de lançamento (release) do app. Obtém a última versão do GitHub, incrementa, atualiza arquivos, gera o instalador e cria a release no GitHub.
---

# Publish Release Skill

Esta skill descreve o passo a passo que o agente deve seguir para automatizar a criação de uma nova release do RadminStreamApp.

## Fluxo de Trabalho

Sempre que acionar esta skill, o agente deve executar as seguintes etapas rigorosamente:

1. **Verificar a Última Versão no GitHub**:
   - Execute o comando `gh release list --limit 1` para obter a última versão (ex: `v1.0.4`).
   - Calcule a **nova versão** incrementando o patch (ex: `1.0.4` -> `1.0.5`), a menos que o usuário solicite um incremento maior (minor ou major). Guarde o número da versão sem o `v` para os arquivos e com o `v` para o git/gh.

2. **Atualizar a Versão** (um único lugar):
   - No arquivo `src/RadminStreamApp/RadminStreamApp.csproj`, procure pela tag `<Version>` e atualize para a nova versão (ex: `<Version>1.0.5</Version>`).
   - **Não edite mais `build/setup.iss` nem `MainWindow.xaml`**: o `AppVersion` do Inno sai do `build/version.iss`
     gerado pelo build, e o texto na tela vem de `AppInfo.Version` (lido do assembly).

3. **Rodar os Testes**:
   - Execute `.\.dotnet\dotnet.exe test RadminStreamLive.sln -c Release`.
   - Se algum teste falhar, interrompa o processo e avise o usuário.

4. **Gerar o Instalador**:
   - Invoque a skill `build-installer` (`/build-installer`) para ler as instruções, ou apenas rode os comandos de build do instalador.
   - Certifique-se de executar o comando que compila o projeto e constrói o `RadminStream_Setup.exe` via Inno Setup (o comando está definido no `build-installer`).

5. **Gerar o Arquivo de Verificação** (obrigatório):
   - O app se recusa a instalar uma atualização sem conferir o hash, então a release
     precisa publicar um `.sha256` junto do instalador:
     ```powershell
     (Get-FileHash RadminStream_Setup.exe -Algorithm SHA256).Hash.ToLower() + "  RadminStream_Setup.exe" | Set-Content RadminStream_Setup.exe.sha256
     ```

6. **Committar as Alterações (Opcional, mas recomendado)**:
   - Verifique o que mudou (`git status`).
   - Adicione o arquivo: `git add src/RadminStreamApp/RadminStreamApp.csproj`
   - Faça o commit: `git commit -m "Bump version to v{NOVA_VERSAO}"`
   - Faça o push: `git push`

7. **Criar a Release no GitHub**:
   - Suba o instalador **e** o arquivo de verificação:
     ```powershell
     gh release create v{NOVA_VERSAO} RadminStream_Setup.exe RadminStream_Setup.exe.sha256 --title "v{NOVA_VERSAO}" --notes "Release automatizada v{NOVA_VERSAO}"
     ```
   - Sem o `.sha256`, quem atualizar verá um aviso de "atualização não verificada".

## Tratamento de Erros
- Caso a compilação ou geração do `RadminStream_Setup.exe` falhe, interrompa o processo e avise o usuário antes de commitar ou tentar criar a release.
- Certifique-se de que o usuário está autenticado no `gh` (GitHub CLI) rodando `gh auth status` previamente se houver alguma suspeita.
