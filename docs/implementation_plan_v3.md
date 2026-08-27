# 🧭 Plano de Mudanças — Radmin Stream Live v3 (UX)

> ✅ **Status: implementado.** Fases 1-5 concluídas, mais as pendências 1-3 herdadas da v1.0.13.
> Base: v1.0.13. Notas de implementação:
> - **Fase 1**: o modo Join Stream foi eliminado; `ViewerSession` virou o único caminho de viewer (o `_client`/`_streamManager` do MainWindow saíram). Layout automático: 1 live tela cheia, 2 em 2 colunas, 3-4 em 2x2, 5+ em 3 colunas; toggle Grade/Abas mantido.
> - **Fase 2**: sidebar com um clique para conectar e outro para sair; `CboFriends` (IP digitado) removido — IP novo só pelo Gerenciar amigos. Sidebar some sozinha quando há uma live só e volta ao encostar o mouse na borda esquerda.
> - **Fase 3**: estados vazios na sidebar, na área de lives e no preview do host; `CboWindows` pré-seleciona a primeira tela. A captura de janelas individuais foi **removida** do `WindowHelper` e do `VideoCapturer` (só telas, conforme decidido).
> - **Fase 4**: `RoomPasswordDialog` serve host (definir, opcional) e viewer (só aparece quando o host responde `AUTH_REQUIRED`). Senha não é salva em disco.
> - **Fase 5**: tooltip no contador resolve IP → apelido, com normalização de `::1` e `::ffff:`.
> - **Áudio**: todas as lives tocam ao mesmo tempo; cada aba/célula tem botão de mudo e volume próprios.
> - Testado com duas instâncias reais (host + viewer em 127.0.0.1): senha, conexão por clique, grid com 2 lives simultâneas, auto-hide da sidebar e tooltip de viewers verificados em execução.

O foco desta rodada é **experiência de uso**, não protocolo: unificar os modos, tirar digitação de IP do caminho, trocar senha por modais no momento certo e acabar com telas vazias sem explicação.

---

## Fase 1 — Unificar "Join Stream" + "Watch Party"

### 1.1 Um único modo "Assistir"

**Problema atual:** há dois fluxos paralelos que fazem a mesma coisa. `Join Stream` conecta em um host (`_client` + `_streamManager` no `MainWindow`), `Watch Party` conecta em vários (`ViewerSession` por amigo). Código duplicado em dois lugares e o usuário precisa escolher o modo **antes** de saber se vai querer ver uma ou duas lives.

**Proposta:** a barra superior passa a ter **dois botões apenas**:

```
[ Transmitir ]  [ Assistir ]
```

No modo Assistir, **tudo passa a ser `ViewerSession`**, inclusive quando há só uma:

- 1 sessão conectada → ocupa a área de vídeo inteira (idêntico ao Join Stream de hoje)
- 2+ sessões → vira grid automaticamente

Assim "estou vendo uma live e quero adicionar outra" é só clicar no próximo amigo — sem trocar de modo, sem desconectar.

**Arquivos afetados:**

- [MainWindow.xaml](MainWindow.xaml) — remover `PanelClient` e `PanelWatchParty`, unificar em `PanelViewer`
- [MainWindow.xaml.cs](MainWindow.xaml.cs) — **apagar** `_client` e o `_streamManager` do lado viewer (~200 linhas: `BtnConnect_Click`, `SetupClientStreamManagerAsync`, `UpdateViewerStatsOverlay`, os handlers de `AUTH_*`/`STREAM_*`), tudo já existe em [ViewerSession.cs](Core/ViewerSession.cs)
- [Core/ViewerSession.cs](Core/ViewerSession.cs) — vira o único caminho de viewer

> [!NOTE]
> Isto elimina a duplicação que causou o bug do `AUTH_OK` ter que ser corrigido em dois lugares. É a maior limpeza do plano e destrava os itens seguintes.

**Ponto de atenção:** hoje o PiP e o overlay de stats do viewer estão ligados ao `_streamManager` do `MainWindow`. Precisam passar a apontar para a **sessão ativa** (aba selecionada / célula focada do grid).

### 1.2 Layout: como mostrar N lives

Substituir o `ToggleButton "Grid"` manual por comportamento automático, mantendo a troca manual como opção:

| Sessões | Layout padrão        |
| ------- | -------------------- |
| 1       | tela cheia           |
| 2       | 2 colunas            |
| 3-4     | 2x2                  |
| 5+      | 3 colunas com scroll |

`UniformGrid` com `Columns` calculado no code-behind conforme `_sessions.Count`. O modo abas continua disponível num toggle `[▦ Grid] [▤ Abas]`.

---

## Fase 2 — Escolher quem assistir

### 2.1 🎯 Sidebar de amigos (substitui a ListBox de seleção múltipla)

**Problema atual:** `LstWatchPartyFriends` é uma `ListBox SelectionMode="Multiple"` de 30px de altura + botão "Conectar Selecionados". Selecionar com ctrl+clique e depois confirmar é ruim, e a lista não cabe na barra.

**Proposta:** painel lateral fixo à esquerda da área de vídeo no modo Assistir (~220px), com um **card por amigo**:

```
┌────────────────────────┐
│ 🟢 João          [ ▶ ] │   ← em live, clique conecta
│    26.10.0.5           │
├────────────────────────┤
│ 🟢 Maria      [ ■ ]    │   ← já assistindo, clique sai
│    26.10.0.9 · 42ms    │
├────────────────────────┤
│ 🟡 Pedro         [ ▶ ] │   ← app aberto, sem live
│    26.10.0.7           │
├────────────────────────┤
│ ⚫ Lucas               │   ← offline, card desabilitado
│    26.10.0.3           │
└────────────────────────┘
[ ⚙ Gerenciar amigos ]
```

- **Um clique conecta, outro clique desconecta.** Sem seleção múltipla, sem botão de confirmar.
- Amigo já conectado fica destacado e mostra a latência ao vivo
- Amigo offline (⚫) fica desabilitado com tooltip "Offline"
- Botão da engrenagem no rodapé abre o `ManageFriendsDialog` que já existe

**Arquivos:** `Controls/FriendCard.xaml` (novo), [MainWindow.xaml](MainWindow.xaml), [MainWindow.xaml.cs](MainWindow.xaml.cs).

### 2.2 ✂️ Remover a digitação de IP

- Apagar o `ComboBox IsEditable` (`CboFriends`) da barra
- Conectar só é possível a partir de um amigo salvo na sidebar
- Adicionar um IP novo passa a ser exclusivamente pelo **⚙ Gerenciar amigos**, que já tem campos Apelido + IP, edição inline e remoção
- No gerenciador, adicionar validação de formato de IP (hoje só valida vazio e duplicado)

---

## Fase 3 — Estados vazios e primeira execução

### 3.1 Avisos de lista vazia

Nenhuma tela deve ficar preta e muda. Onde falta:

| Onde                                     | Estado vazio                                                                        |
| ---------------------------------------- | ----------------------------------------------------------------------------------- |
| Sidebar de amigos                        | "Nenhum amigo adicionado ainda" + botão **➕ Adicionar amigo** (abre o gerenciador) |
| Área de vídeo (Assistir, nada conectado) | "Clique em um amigo à esquerda para assistir"                                       |
| Área de vídeo (Transmitir, parado)       | manter "No Signal", mas com dica "Escolha uma tela e clique em Start"               |
| Gerenciador de amigos                    | ✅ já feito (`TxtEmpty`)                                                            |
| Nenhum amigo online                      | "Nenhum amigo online no momento" abaixo da lista                                    |

### 3.2 🖥️ Seleção de tela pré-preenchida

**Bug atual:** o `CboWindows` abre vazio ([MainWindow.xaml:262](MainWindow.xaml#L262)) — `GetCapturableWindows()` é chamado, mas nada fica selecionado, então o campo aparece em branco até o usuário abrir o dropdown.

**Correção:** após popular, `CboWindows.SelectedIndex = 0` (a primeira tela). Vale fazer nos dois pontos onde a lista é carregada: `BtnHost_Click` e `CboWindows_DropDownOpened` (este só se ainda não houver seleção, para não trocar a fonte durante uma live).

> [!IMPORTANT]
> Descoberta durante a análise: `WindowHelper.GetCapturableWindows()` hoje retorna **apenas monitores** ([WindowHelper.cs:75](Helpers/WindowHelper.cs#L75)) — o `EnumWindows` e os filtros de janela (cloaked, tool window, owner) estão declarados mas **não são usados**. Então o rótulo "Selecione uma janela para transmitir" está errado: só dá para transmitir telas inteiras.
> **Decidir:** (a) só corrigir o texto para "tela", ou (b) reativar a enumeração de janelas individuais. Ver Perguntas Abertas.

---

## Fase 4 — Senha por modal, no momento certo

### 4.1 🔑 Host: senha ao clicar em Start

**Hoje:** `TxtRoomPassword` fica sempre visível na barra, ocupando espaço mesmo em sala aberta.

**Proposta:** remover o campo da barra. Ao clicar em **Start Stream**, abre `RoomPasswordDialog`:

```
┌─────────────────────────────────┐
│  Senha da sala                  │
│                                 │
│  Deixe em branco para que       │
│  qualquer amigo possa entrar.   │
│                                 │
│  [_______________________] 👁    │
│                                 │
│      [ Cancelar ]  [ Iniciar ]  │
└─────────────────────────────────┘
```

- Vazio → sala aberta, transmissão começa (comportamento atual quando `RoomPassword` é `""`)
- Preenchido → `_server.RoomPassword = senha`, que já ativa a criptografia AES do payload
- Cancelar → não inicia a transmissão
- Lembrar a última senha usada na sessão (pré-preencher no próximo Start)

### 4.2 🔓 Viewer: senha só quando o host pedir

**Hoje:** há dois campos de senha (`TxtClientPassword` e `TxtWatchPartyPassword`) que o usuário precisa preencher **antes** de conectar, adivinhando se a sala tem senha.

**Proposta:** remover os dois campos. O protocolo já resolve isso — o fluxo passa a ser:

1. Clique no amigo → conecta direto
2. Sala aberta → live abre, sem perguntar nada
3. Sala com senha → o servidor responde `AUTH_REQUIRED` → **aí** abre o modal pedindo a senha
4. `AUTH_FAIL` → modal reabre com "Senha incorreta", com opção de cancelar
5. `AUTH_OK` → guarda a senha na `ViewerSession` para reconectar sem perguntar de novo

**Arquivos:** `RoomPasswordDialog.xaml` (novo, serve aos dois casos), [ViewerSession.cs](Core/ViewerSession.cs) (`AUTH_REQUIRED` passa a disparar um evento em vez de ler um TextBox), [MainWindow.xaml](MainWindow.xaml).

---

## Fase 5 — Saber quem está assistindo

### 5.1 👥 Tooltip no contador de viewers

**Hoje:** `ViewerCountText` mostra só "3 Viewers".

**Proposta:** passar o mouse mostra a lista de quem está conectado, resolvendo IP → apelido:

```
   3 Viewers
   ┌──────────────────────┐
   │ João                 │  ← IP bate com um amigo salvo
   │ Maria                │
   │ 26.10.0.44           │  ← desconhecido, mostra o IP
   └──────────────────────┘
```

**Como:**

1. `SignalingServer` expõe `ConnectedClientIps` (lista de `ConnectionInfo.ClientIpAddress`, sob o `_clientsLock` que já existe)
2. `MainWindow` cruza cada IP com `_friends` e usa `Friend.Name` quando houver correspondência
3. Atualizar em `OnClientConnected` / `OnClientDisconnected`, que já chamam `UpdateViewerCount()`

> [!WARNING]
> **Detalhe técnico:** o Fleck pode devolver IPv4 mapeado em IPv6 (`::ffff:26.10.0.5`) e conexões locais como `::1`. A comparação precisa normalizar antes de bater com o `Friend.Ip`, senão o nome nunca vai aparecer.

**Extra barato:** o mesmo mapeamento permite trocar `RemoveClient(id)` por logs legíveis ("João saiu") e, se quiser depois, um aviso "João entrou na sua live".

---

## 📋 Resumo

| Fase | Item                                    | Esforço | Risco                                           |
| ---- | --------------------------------------- | ------- | ----------------------------------------------- |
| 1    | Unificar Join + Watch Party             | Alto    | **Médio** — mexe no caminho principal do viewer |
| 2    | Sidebar de amigos + remover IP digitado | Médio   | Baixo                                           |
| 3    | Estados vazios + tela pré-selecionada   | Baixo   | Baixo                                           |
| 4    | Senha por modal (host e viewer)         | Médio   | Baixo                                           |
| 5    | Tooltip de viewers com nomes            | Baixo   | Baixo                                           |

**Ordem sugerida:** 3 → 5 → 4 → 2 → 1.
As fases 3 e 5 são rápidas e independentes (ganho imediato). A fase 1 é a mais invasiva e fica por último, quando a sidebar (fase 2) já definiu como as sessões são criadas.

Alternativa: se quiser ver o resultado grande primeiro, dá para fazer 1 → 2 juntas numa sessão só, mas aí a interface fica quebrada no meio do caminho.

---

## 🧹 Pendências herdadas da auditoria da v1.0.13

Não vieram dos seus comentários, mas continuam abertas — decidir se entram nesta rodada:

1. **Reconexão após o host encerrar:** `STREAM_STOPPED` não marca desconexão intencional, então se o host parar a live e fechar o app, o viewer tenta 10 reconexões inúteis
2. **`ViewerSession` sobrescreve "Transmissão Encerrada"** com `WebRTC: closed` (o `MainWindow` tem guarda para isso, a sessão não)
3. **Polling de status a cada 10s** — o plano v2 dizia 30s; 10s × N amigos é bastante conexão TCP à toa
4. **`SIPSorcery` 8.0.7 com 2 advisories de severidade alta** (NU1903)

---

## ❓ Perguntas Abertas

1. **Captura de janela:** reativar a enumeração de janelas individuais (compartilhar só o Chrome, só o jogo) ou assumir de vez que é captura de tela inteira e ajustar os textos?
   → Quero que mantenha apenas as telas, pode remover qualquer codigo que tiver de compartilhar apps ou coisas especificas

2. **Sidebar sempre visível?** No modo Assistir com uma live em tela cheia, a sidebar rouba ~220px. Prefere (a) sempre visível, (b) recolhível com um botão `☰`, ou (c) some sozinha quando há uma live só e volta ao mover o mouse?
   → some sozinha quando há uma live só e volta ao mover o mouse

3. **Áudio com várias lives:** hoje cada sessão toca seu próprio áudio ao mesmo tempo. Quer (a) manter tudo tocando, (b) só a aba/célula ativa toca, ou (c) todas mudas menos a que você clicar?
   → sai o som de todas a pessoa tem que mutar a que quiser

4. **Amigo offline na sidebar:** esconder da lista ou mostrar apagado no fim? (proposta acima: mostrar apagado)
   → mostrar apagado no fim

5. **Senha salva:** guardar a senha de cada sala junto do amigo no `friends.json` para não digitar toda vez? Fica gravada em texto puro no `%LocalAppData%`.
   → a senha vai ser algo que a pessoa vai criar na hora de stremar nao precisa salvar
