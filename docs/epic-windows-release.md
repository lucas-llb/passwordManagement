# Épico: lançamento da versão Windows 1.0

> Status: **planejamento** · Plataforma: Windows 10 (19041+) / 11, x64 e ARM64
> Regras de segurança, formato e convenções: [AGENTS.md](../AGENTS.md), [vault-format.md](vault-format.md), [threat-model.md](threat-model.md).

## 1. Objetivo

Publicar a primeira versão estável do app Windows: um gerenciador de senhas offline, com múltiplos perfis, que cria, abre e edita cofres `.pmvault` no formato v1 **congelado**, e que exporta arquivos que o app Android vai conseguir abrir.

**Pronto quando:** um usuário instala o MSIX assinado, cria um perfil, cadastra entradas, bloqueia/desbloqueia, exporta para `.pmvault` e CSV e importa de volta, sem que o app tenha qualquer capacidade de rede, e todos os itens da [seção 7](#7-checklist-de-release-definition-of-done) estão marcados.

## 2. Escopo

**Dentro (MVP da seção 1 do AGENTS.md, versão Windows):**

- Perfis múltiplos, cada um com cofre e senha mestra próprios.
- Desbloqueio por senha mestra (Argon2id) com atraso progressivo após erros.
- Kit de Recuperação (decisão D1).
- CRUD de entradas, busca local, favoritos, pastas.
- Gerador de senhas e cópia para a área de transferência com limpeza em 30 s, fora do histórico e da nuvem.
- Bloqueio automático: inatividade, bloqueio da sessão do Windows, opcionalmente ao minimizar.
- Troca de senha mestra.
- Exportação/importação `.pmvault` e CSV (colunas Bitwarden/KeePass).
- Configurações em uma tela só.
- Proteção contra captura de tela (`WDA_EXCLUDEFROMCAPTURE`).
- Pacote MSIX assinado, sem `internetClient`.

**Fora:** Windows Hello, sync, extensão de navegador, autoatualização pela rede, telemetria/crash reporting, app Android (só a parte de `core` necessária para validar os vetores, ver seção 5).

## 3. Ponto de partida (out/2026)

| Item | Estado |
|---|---|
| Solução `.slnx`, CPM, lockfiles, analisadores, warnings como erro | feito |
| `PasswordManager.Core`: modelos (`VaultEntry`, `VaultContent`), constantes (`VaultFormat`), JSON source-gen | feito |
| Testes: só serialização JSON | parcial |
| Cripto (KDF, wrap/unwrap, AEAD), leitura/escrita do arquivo | não iniciado |
| App WinUI: janela vazia com título vindo do `.resw` | esqueleto |
| Empacotamento: `WindowsPackageType=None` (sem ícones) | pendente |
| `spec/test-vectors/`: só README | pendente |

## 4. Decisões que precisam sair antes de começar

Estas mudam o formato ou a distribuição; decidir antes das fases que dependem delas.

| # | Decisão | Bloqueia | Recomendação |
|---|---|---|---|
| D1 | Kit de Recuperação entra na 1.0? Se sim, onde fica o segundo envelope da Vault Key | congelamento do formato (F1) | **Decidido (W-01):** Kit na 1.0 = slot tipo 2 da tabela de slots; senha = slot tipo 1; biometria/Windows Hello ficam em arquivo local separado (AGENTS §4.1), fora do cabeçalho. Ver vault-format.md. |
| D2 | Canal de distribuição: Microsoft Store, MSIX assinado no GitHub Releases, winget | F6 | Store (assinatura e atualização feitas pela loja) + MSIX no GitHub Releases para quem não usa a loja. A loja não exige `internetClient` no app. |
| D3 | Certificado de assinatura para o MSIX fora da loja | F6 | Certificado de code signing (OV ou Azure Trusted Signing). Chave **fora do repositório**. |
| D4 | Medidor de força da senha | F3 | **Decidido (W-16):** estimador próprio no `Core` (`PasswordStrength`), sem dependência nova; port de zxcvbn descartado (pacote novo, dicionários grandes). Menor custo em bits entre força bruta, palavras (lista EFF curta + lista própria de senhas comuns), repetições, sequências e anos. Medidor só informativo; a regra que bloqueia continua sendo ≥ 12 caracteres. |
| D5 | Trimming do app WinUI | F6 | Manter `PublishTrimmed` só se o app passar no teste manual completo trimado; senão desligar no App e manter `IsTrimmable` no Core. |
| D6 | Versão mínima do Windows | F6 | Manter `10.0.19041` (Windows 10 2004). Confirmar que `WDA_EXCLUDEFROMCAPTURE` (2004+) e as APIs de clipboard funcionam nela. |

## 5. Fases e histórias

IDs `W-xx` para referência em branches/PRs. Cada fase termina com `dotnet test --solution windows/PasswordManager.slnx` verde.

### F1 — Core: cripto e formato do cofre

Toda a lógica fica em `PasswordManager.Core`, testável sem UI.

| ID | História | Critérios de aceite |
|---|---|---|
| W-01 | Fechar o layout v1 com a decisão D1 | `vault-format.md` atualizado (sem "rascunho"); `VaultFormat.cs` e `VaultFormat.kt` espelham as constantes. |
| W-02 | Derivação da KEK com Argon2id (NSec) | Parâmetros lidos do cabeçalho; senha em `byte[]`/`char[]` zerada após uso; teste com valores conhecidos. |
| W-03 | Wrap/unwrap da Vault Key (XChaCha20-Poly1305) | AAD = cabeçalho conforme spec; senha errada → erro tipado, sem detalhe do motivo; chaves zeradas com `CryptographicOperations.ZeroMemory`. |
| W-04 | Cifrar/decifrar o corpo JSON | Nonce novo a cada gravação; byte alterado no cabeçalho ou no corpo → falha (teste para cada região). |
| W-05 | Leitura/escrita do arquivo `.pmvault` | Escrita atômica: temporário → `Flush(true)` → substituir original; `.bak` da versão anterior; arquivo truncado ou magic/versão inválidos → erro claro. |
| W-06 | Repositório de perfis (`profiles.json`) | Criar, listar, renomear, excluir perfil (excluir pede confirmação na UI e remove `.pmvault` e `.bak`); pasta `%LOCALAPPDATA%\<App>\vaults\` ou `LocalFolder` quando empacotado. |
| W-07 | Troca de senha mestra | Vault Key não muda; só o slot de senha é re-envolvido (salt e nonce novos); slot do Kit intacto; corpo re-selado com a mesma Vault Key e nonce novo (cabeçalho é AAD); senha antiga deixa de abrir, nova abre. |
| W-08 | Kit de Recuperação | Chave de 128+ bits do CSPRNG, formato legível (grupos de caracteres); slot tipo 2; texto Crockford base32 8×4 conforme vault-format.md; abre o cofre sem a senha mestra; permite definir nova senha. |
| W-09 | Fachada `VaultSession` | API única para a UI: `Unlock`, `Lock`, `Save`, `ChangePassword`, `Export`, `Import`. `Lock` zera chaves e descarta o modelo. |
| W-10 | Vetores de teste compartilhados | `spec/test-vectors/` com ao menos: cofre vazio, cofre com entradas (inclusive Unicode/emoji), cofre com Kit de Recuperação, exportação. Teste do Core abre todos. |

**Dependência cruzada:** o formato v1 só é considerado congelado quando o `core` Android também passar nos vetores de W-10 (seção 8 do AGENTS.md). Isso exige implementar a cripto do `core` Android em paralelo (sem UI). Depois do lançamento, qualquer mudança vira v2.

### F2 — App: fundação

| ID | História | Critérios de aceite |
|---|---|---|
| W-11 | Estrutura MVVM e navegação | `CommunityToolkit.Mvvm`; navegação Perfil → Desbloqueio → Lista → Detalhe; ViewModels testáveis sem janela. |
| W-12 | Recursos e localização | Todo texto em `Strings/pt-BR/Resources.resw`; nenhum literal de UI no código. |
| W-13 | Tema e acessibilidade | Segue tema do sistema; `AutomationProperties.Name` em todos os controles interativos; navegação completa por teclado; contraste AA. |
| W-14 | Proteção da janela | `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` ligado enquanto o cofre estiver aberto; verificado com Ferramenta de Captura e OBS. |

### F3 — App: perfis e desbloqueio

| ID | História | Critérios de aceite |
|---|---|---|
| W-15 | Tela de seleção de perfil | Lista nomes de `profiles.json`; criar/renomear/excluir. |
| W-16 | Criação de perfil | Senha mestra ≥ 12 caracteres, medidor de força (D4), sugestão de frase-senha, aviso explícito de que **não há recuperação** pelo app; exibe o Kit de Recuperação uma vez. |
| W-17 | Desbloqueio | Argon2id fora da thread de UI com indicador de progresso; atraso progressivo 1, 2, 4… até 30 s; tempo de desbloqueio medido em máquina modesta. |
| W-18 | Bloqueio automático | Inatividade (padrão 5 min), bloqueio da sessão do Windows (`SessionSwitch` ou `WTSRegisterSessionNotification`), opcional ao minimizar; ao bloquear a UI volta ao desbloqueio sem resquícios das entradas. |

### F4 — App: entradas

| ID | História | Critérios de aceite |
|---|---|---|
| W-19 | Lista com busca | Busca local por título, usuário, URL e pasta; filtros favoritos/pasta; responsivo com 2.000 entradas. |
| W-20 | Detalhe e edição | Todos os campos do `VaultEntry`; senha oculta por padrão com botão mostrar; `createdAt`/`updatedAt` mantidos; salvar grava atomicamente; inclui criar e excluir entrada. |
| W-21 | Gerador de senhas | Embutido no formulário; tamanho (padrão 20), maiúsculas, números, símbolos; `RandomNumberGenerator`, sem viés de módulo; testes no Core. |
| W-22 | Área de transferência | `ClipboardContentOptions` com `IsAllowedInHistory = false` e `IsRoamable = false`; limpa após 30 s só se o conteúdo ainda for o copiado; testado com Win+V ligado. |

### F5 — Exportação, importação e configurações

| ID | História | Critérios de aceite |
|---|---|---|
| W-23 | Exportação `.pmvault` | Senha de exportação própria; Vault Key nova; `FileSavePicker`; nunca grava em temp/cache. |
| W-24 | Exportação CSV | Pede a senha mestra de novo; aviso claro de arquivo legível; colunas `name,url,username,password,notes,folder`; escaping correto (vírgula, aspas, quebra de linha). |
| W-25 | Importação | `.pmvault` próprio e CSV Bitwarden/KeePass; validação completa; prévia antes de gravar; tratamento de duplicados definido (pular, duplicar ou substituir). |
| W-26 | Tela de configurações | Tempo de inatividade, bloquear ao minimizar, trocar senha mestra, gerar novo Kit de Recuperação, exportar/importar. Persistência fora do cofre só para preferências não sensíveis. |

### F6 — Empacotamento e distribuição

| ID | História | Critérios de aceite |
|---|---|---|
| W-27 | Ícones e identidade visual | Ícone do app e logos MSIX em todas as escalas exigidas. Arte gerada conforme as regras globais de imagem. |
| W-28 | Migração para MSIX | `WindowsPackageType` padrão (empacotado); manifesto **sem** `internetClient` nem outra capability de rede; cofre em `ApplicationData.Current.LocalFolder`. |
| W-29 | Build de release | Self-contained x64 e ARM64; trimming conforme D5; ReadyToRun; medir tamanho do pacote e tempo de inicialização. |
| W-30 | Assinatura e publicação | Conforme D2/D3; chave fora do repositório; processo de release documentado (versionamento, changelog, hash do pacote). |
| W-31 | Auditoria de dependências | Lista final de pacotes NuGet revisada contra a seção 7 do AGENTS.md; nenhuma dependência transitiva de rede/telemetria. |

### F7 — Garantia de qualidade e segurança

| ID | História | Critérios de aceite |
|---|---|---|
| W-32 | Revisão de segurança | Checagem contra `threat-model.md`; busca por logs/`ToString()` com segredos; revisão de zeragem de chaves no `Lock`. |
| W-33 | Teste de "offline de verdade" | App rodando com o firewall registrando conexões: zero tentativas de rede durante o uso completo. |
| W-34 | Roteiro de teste manual | Roteiro em `docs/` cobrindo o fluxo completo, em Windows 10 e 11, x64 e ARM64 (se houver máquina), tema claro/escuro, Narrador, escala 100/150/200%. |
| W-35 | Testes de robustez | Queda de energia simulada durante a gravação (processo morto entre temp e rename) → cofre ou `.bak` abre; disco cheio → erro sem corromper. |

## 6. Ordem e dependências

```
D1 ──► F1 (W-01…W-10) ──► F3, F4, F5
           │
           └─► core Android passa nos vetores ──► formato v1 congelado ──┐
F2 (pode começar junto com F1) ──► F3 ──► F4 ──► F5                       │
D2, D3, D5, D6 ──► F6 ────────────────────────────────────────────────────┤
                                                                         ▼
                                                              F7 ──► Release 1.0
```

- F1 e F2 podem andar em paralelo; F3 em diante precisam de `VaultSession` (W-09).
- W-27 (ícones) pode começar já: é ele que destrava a migração para MSIX (W-28).
- Recomenda-se migrar para MSIX cedo (logo após F3), para que F4–F7 já sejam testadas empacotadas.

## 7. Checklist de release (Definition of Done)

- [ ] Todas as histórias W-01…W-35 concluídas (ou explicitamente adiadas com motivo neste arquivo).
- [ ] `dotnet test --solution windows/PasswordManager.slnx` verde, cobrindo: derivação, wrap/unwrap, cifrar/decifrar, senha errada, adulteração de cabeçalho e de corpo, troca de senha, export/import.
- [ ] Core Android abre todos os vetores de `spec/test-vectors/`; `vault-format.md` marcado como v1 final.
- [ ] MSIX sem `internetClient`; nenhuma conexão observada em W-33.
- [ ] `WDA_EXCLUDEFROMCAPTURE`, limpeza de clipboard e bloqueio automático verificados manualmente.
- [ ] Roteiro W-34 executado e registrado.
- [ ] Nenhum segredo, certificado, `.pfx` ou cofre real no repositório.
- [ ] Notas de versão em pt-BR, incluindo o aviso de que não há recuperação de senha sem o Kit.

## 8. Riscos

| Risco | Impacto | Mitigação |
|---|---|---|
| Formato precisar mudar logo após a release (ex.: Kit de Recuperação, Windows Hello) | v2 cedo, migração de cofres | D1 decidido em W-01: tabela de slots. |
| Divergência Windows ↔ Android no formato | exportação não abre no outro app | Vetores compartilhados como portão obrigatório; mesma libsodium nas duas pontas. |
| Trimming quebrar WinUI/reflexão | crash só no build de release | D5; testar sempre o pacote de release, não só o Debug. |
| Argon2id lento em máquinas fracas | desbloqueio travando a UI | Rodar fora da thread de UI; medir (medido na W-17, [architecture.md](architecture.md) §4.3: ~40 ms na máquina de desenvolvimento; a linha da máquina modesta está pendente); parâmetros já ficam no cabeçalho para ajuste futuro. |
| Custo/burocracia de assinatura e Store | atraso na publicação | Iniciar D2/D3 cedo, em paralelo com F1. |
| Dependência nova para medidor de força | aumento de superfície | D4 decidido sem dependência (W-16). |
