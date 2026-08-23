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

2. **Atualizar Versões nos Arquivos**:
   - No arquivo `RadminStreamApp.csproj`, procure pela tag `<Version>` e atualize para a nova versão (ex: `<Version>1.0.5</Version>`).
   - No arquivo `setup.iss`, procure por `AppVersion=` e atualize para a nova versão (ex: `AppVersion=1.0.5`).
   - No arquivo `MainWindow.xaml`, na seção de informações/configurações, procure pelo texto da versão e atualize para a nova versão (ex: `Text="Versão 1.0.5"`).

3. **Gerar o Instalador**:
   - Leia as instruções da skill `build-installer` se precisar, ou apenas rode os comandos de build do instalador.
   - Certifique-se de executar o comando que compila o projeto e constrói o `RadminStream_Setup.exe` via Inno Setup (o comando está definido no `build-installer`).

4. **Committar as Alterações (Opcional, mas recomendado)**:
   - Verifique se os arquivos `RadminStreamApp.csproj`, `setup.iss` e `MainWindow.xaml` foram modificados (`git status`).
   - Adicione os arquivos: `git add RadminStreamApp.csproj setup.iss MainWindow.xaml`
   - Faça o commit: `git commit -m "Bump version to v{NOVA_VERSAO}"`
   - Faça o push: `git push`

5. **Criar a Release no GitHub**:
   - Execute o comando para criar a release e fazer o upload do executável recém gerado:
     ```powershell
     gh release create v{NOVA_VERSAO} RadminStream_Setup.exe --title "v{NOVA_VERSAO}" --notes "Release automatizada v{NOVA_VERSAO}"
     ```

## Tratamento de Erros
- Caso a compilação ou geração do `RadminStream_Setup.exe` falhe, interrompa o processo e avise o usuário antes de commitar ou tentar criar a release.
- Certifique-se de que o usuário está autenticado no `gh` (GitHub CLI) rodando `gh auth status` previamente se houver alguma suspeita.
