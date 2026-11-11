# Revisão de segurança (W-32)

> Data: 2026-11-11 · Base: commit `d4696ff` mais a correção da página de Desbloqueio desta história, ainda não commitada quando a revisão foi escrita (o código citado é o da árvore com essa correção).
> Escopo: o app Windows (`windows/`). O Android fica fora: o `:core` ainda não tem `VaultSession` ([architecture.md](architecture.md) §10).

## Método e escopo

Revisão **estática**: leitura do código de `windows/src`, dos três projetos de teste e do [threat-model.md](threat-model.md), mais as varreduras de `rg` e os comandos do fim deste documento. **Não houve inspeção dinâmica da memória** — nem depurador, nem despejo de processo, nem leitura do heap em execução. Tudo o que se afirma sobre zeragem vem do código e dos testes que leem os buffers. Também **não** houve teste de tráfego de rede (W-33) nem roteiro manual (W-34): nenhum dos dois foi executado, e esta revisão não os substitui.

A ordem foi: (1) cada linha de `docs/threat-model.md` contra o código que a implementa e os testes que a pinam; (2) a varredura de log e de `ToString()` em `windows/src`; (3) a zeragem de chaves em `VaultSession.Lock`/`Dispose` e nos temporários das classes que os possuem; (4) os sete itens que `docs/architecture.md` §13 deixou "revisar na W-32".

Os testes citados são a evidência executável: eles rodam na validação da história, `cd windows; dotnet test --solution PasswordManager.slnx` (os E2E só em Debug e com desktop interativo). A revisão conferiu o nome e a asserção de cada um; os números entre parênteses são os da suíte em [architecture.md](architecture.md) §11.

## Modelo de ameaças: linha a linha

| Ameaça (threat-model.md) | Proteção declarada | Onde está no código | Testes que pinam | Veredito |
|---|---|---|---|---|
| Alguém copia o arquivo do cofre (backup, disco roubado, exportação vazada) | Argon2id + XChaCha20-Poly1305; nada legível sem a senha | `Crypto/Argon2idKdf.cs` (`DeriveKek`), `Vault/KeySlot.cs` (`CreatePasswordSlot`/`UnwrapPasswordSlot`), `Crypto/VaultKeyWrap.cs`, `Vault/VaultBodyCipher.cs` | `Argon2idKdfTests`, `VaultKeyWrapTests`, `KeySlotTests` (known-answer F1/F2; senha errada e salt adulterado dão a mesma exceção), `VaultSessionTests.Unlock_RightPassword_IsUnlockedWithEntries`, `TestVectorTests` | **Coberto.** Argon2id (64 MiB/3/1) mais XChaCha20-Poly1305; sem a senha ou o kit, nada do corpo abre. |
| Adulteração do arquivo | Cabeçalho e corpo autenticados (AEAD) | `Vault/VaultHeader.cs` (`Parse`, validação completa), `Vault/VaultBody.cs` (`Open`/`Seal`, cabeçalho inteiro como AAD), `Vault/KeySlot.cs` (`AAD_wrap` por slot) | `VaultHeaderTests`, `VaultBodyTests`, `KeySlotTests` (byte trocado nas bordas do slot), `VaultSessionTests.Unlock_TamperedBody_ThrowsZeroesKeyAndStaysLocked`, os testes de adulteração byte a byte de cada região | **Coberto.** Qualquer byte do cabeçalho ou do corpo invalida a tag AEAD. |
| Pessoa com acesso ao aparelho desbloqueado | Bloqueio automático por inatividade e ao sair do app; biometria exige `CryptoObject` | `ViewModels/AutoLock.cs` (inatividade, sessão do Windows, minimizar), mensagens `WM_WTSSESSION_CHANGE` e `WM_SIZE` na `MainWindow`, `ViewModels/UnlockThrottle.cs` | `AutoLockTests`, `AutoLockE2ETests` (5), `UnlockThrottleTests`, `UnlockViewModelTests` (26) | **Coberto.** Limite declarado: o E2E posta a mensagem de sessão, não prova a entrega do Windows (§13); o atraso do desbloqueio é só de UI e a proteção real é o Argon2id. |
| Captura de tela / gravação / miniatura em "recentes" | `FLAG_SECURE` (Android); `WDA_EXCLUDEFROMCAPTURE` (Windows) | `ViewModels/PageViewModel.cs` (`AllowsScreenCapture`, padrão `false`), `MainWindow.OnNavigated` (`Protect` antes do `Frame.Navigate`, `ScheduleUnprotect` depois do primeiro quadro), `NativeMethods.SetWindowDisplayAffinity` | `ScreenCaptureTests` (10), `WindowProtectionE2ETests` (2), `ThemeE2ETests` (1), `ScreenPixelsTests` (4 de `ChangedFraction`) | **Coberto.** Só a página de Perfis é capturável; desde a W-32 o Desbloqueio também fica excluído. Limites em §9.6: miniaturas da barra de tarefas e do Alt+Tab, Área de Trabalho Remota e câmera apontada para a tela não são cobertos. O teste manual com a Ferramenta de Captura é o item 4 da validação e **não** foi executado por esta revisão. |
| Senha copiada esquecida na área de transferência | Limpeza após 30 s; fora do histórico e do clipboard em nuvem | `ViewModels/ClipboardGuard.cs` (30 s, `ExpireNow` no bloqueio, `string` só até a cópia ser resolvida), `App/WindowsClipboard.cs` (marcadores de histórico e nuvem) | `ClipboardGuardTests` (31), `EntryCopyTests` (22), `ClipboardE2ETests` (7) | **Coberto.** Limite declarado: com a nuvem da área de transferência desligada, `IsRoamable` só se confere pelo marcador `CanUploadToCloudClipboard` (§13). |
| Arquivo de importação hostil | Limites antes de alocar (32 MiB, 20.000 itens, 65.536 caracteres por campo, 64 colunas); CSV UTF-8 estrito; `.pmvault` só chega à prévia depois do AEAD; uma só mensagem para senha errada e arquivo adulterado; nada gravado antes de confirmar; prévia sem senha; o bloqueio não espera o Argon2id | `Vault/VaultImport.cs` (limites, `ReadFile` com array fixado), `Vault/VaultCsv.cs` (`ParseAndZero`, UTF-8 estrito), `VaultSession.Import` (Argon2id fora da trava) | `VaultImportTests` (41), `VaultCsvImportTests` (53), `ImportViewModelTests` (34), `ImportE2ETests` (17 casos), `VaultSessionTests.Import_LockDuringKdf_DoesNotWait` | **Coberto.** Limites antes de alocar, nada gravado antes de confirmar, prévia sem senha. Limites declarados: um arquivo de custo máximo mantém a página ocupada por dezenas de segundos, e o caminho de falta de memória (`CryptographicException`) não é testável (§13). |
| Alguém edita o `settings.json` das preferências | O arquivo não tem nada de um cofre e os valores são limitados; não é autenticado | `Preferences/PreferencesStore.cs` (4 KiB, `Load` nunca lança e nunca grava), `Preferences/AppPreferences.cs` (só 1/2/5/10/15/30/60 min; fora disso, 5 min) | `PreferencesStoreTests` (46), `AppSettingsTests` (13), `SettingsE2ETests` (19 casos) | **Coberto.** O arquivo não tem nada de um cofre e os valores são limitados; não é autenticado, então quem escreve na pasta de cofres pode pôr 60 min e desligar o bloqueio ao minimizar — o pior caso já declarado na linha do modelo de ameaças. Não existe "nunca bloquear". |
| Uma cópia antiga do `.pmvault` com uma credencial antiga | **Não protege:** a Vault Key é fixa por perfil; o `.bak` ao lado é refeito, as cópias por fora não | Vault Key fixa por perfil (`Vault/VaultSession.cs`, `Vault/MasterPassword.cs`); `Vault/VaultFile.cs` (`WriteCredentialChange`/`RefreshBackup`) refaz só o `.bak` ao lado do cofre | `VaultFileTests` (`RefreshBackup`, `WriteCredentialChange`), `VaultSessionTests` (troca e `.bak` preso), `SettingsE2ETests.Settings_ChangePassword_NewOpensOldFails`, `SettingsE2ETests.Settings_NewKit_ShownOnceAndOldStopsWorking` | **Não protege por desenho.** A cópia antiga ainda abre com a credencial que valia nela e entrega a mesma Vault Key; só apagá-la resolve. A tela diz isso (`SettingsPasswordNote` e `SettingsKitNote`). |
| Vazamento por rede ou telemetria | Sem permissão/capability de internet; sem dependências de rede | `App/Package.appxmanifest` (só `runFullTrust`, sem `internetClient`), nenhum pacote de rede no grafo embarcado ([dependency-audit.md](dependency-audit.md)) | `PackageManifestTests` (6), `DependencyAuditTests` (9) | **Coberto no estático.** A prova em tempo de execução é a W-33, **não executada**; esta revisão não afirma zero conexões. |
| Vazamento por logs | Proibido logar segredos; `toString()` das entradas não mostra segredos | Nenhuma API de log em `windows/src` (varredura abaixo); `Vault/VaultEntry.cs` e `ViewModels/EntryDetailViewModel.cs` são os dois únicos `ToString()` | `VaultJsonTests.Entry_ToString_DoesNotLeakSecrets`; guardas da W-32: `SecretGuardTests` (4) e `SecretHygieneTests` (2) | **Coberto.** Ver a seção seguinte. |

**Fora do alcance**, como no modelo de ameaças e mantido sem mudança: malware com privilégios no dispositivo enquanto o cofre está aberto (keylogger, leitura de memória); senha mestra fraca escolhida pelo usuário (mitigada só pelo medidor e pelo custo do Argon2id); aparelho com root/jailbreak. A prova de rede em execução é a W-33 e o roteiro manual é a W-34: **nenhuma das duas foi rodada**.

## Segredos em log e em ToString()

A varredura em `windows/src` não devolveu **nenhum resultado** para API de log:

```
rg -n "Console\.|Debug\.Write|Debug\.Print|Trace\.|ILogger|EventLog|SecureString" windows/src
rg -n "Console\.|Debug\.Write|Debug\.Print|Trace\.|TraceSource|ILogger|EventLog|Debugger\.|SecureString" windows/src   # lista da guarda da W-32
```

O projeto não tem camada de log, `Debugger.`/`TraceSource`, `SecureString` (proibido pela AGENTS §3.2) nem escrita em `EventLog`. Um erro só chega ao usuário como estado do ViewModel, e a View escolhe o texto do `.resw`; mensagens de exceção são em inglês, para o desenvolvedor, e nunca vão para a tela (architecture.md §9.4).

Os **dois únicos** `ToString()` sobrescritos:

```
rg -n "override string ToString" windows/src
  src/PasswordManager.Core/Vault/VaultEntry.cs:17
  src/PasswordManager.ViewModels/EntryDetailViewModel.cs:457
```

- `VaultEntry.ToString()` imprime **só `Id` e `Title`** (o título imprime por decisão documentada); `Password`, `Username`, `Url` e `Notes` **não** aparecem. É exatamente o que `VaultJsonTests.Entry_ToString_DoesNotLeakSecrets` afirma.
- `EntryDetailViewModel.ToString()` imprime só o nome do tipo (`nameof(EntryDetailViewModel)`), nunca o rascunho.

O resto do modelo também não imprime conteúdo: `VaultContent` é um `record` cuja lista imprime o tipo, não as entradas (cada uma tem o `ToString` auditado); `ImportFile`, `ImportPlan` e `ImportMergeResult` são `class` justamente para o gerador de `record` não imprimir entradas (architecture.md §4.2); `RecoveryKitReplacement` não sobrescreve `ToString` e guarda só texto cifrado.

**Política de exceções** (o que impede um segredo de sair por uma mensagem):

- `VaultAuthenticationException` tem a mensagem fixa `"Vault authentication failed."`, **sem `InnerException`** e sem motivo: senha errada, kit errado e arquivo adulterado não se distinguem.
- `VaultBody.Parse` captura `JsonException` e lança `VaultFormatException` **sem encadear a interna**, com o comentário no código de que a mensagem dela pode citar segredos decifrados.
- `VaultCsv.ParseAndZero` não encadeia `DecoderFallbackException`, que guardaria os bytes não decodificados.
- `VaultImportException` leva o motivo e a linha, nunca conteúdo; `ProfileStoreException` não leva nomes de perfil.
- Os ViewModels traduzem a exceção em estado (`UnlockError`, `ImportError`, `PasswordError`…); nenhuma mensagem de exceção é exibida.

As guardas da W-32 mantêm isso assim: `Sources_UseNoLoggingApi` (com controle positivo), `Sources_UseNoSecureString` e `Sources_OverrideToStringOnlyWhereAudited`, que reprovam um `ToString()` novo até ele entrar na lista auditada, mais `VaultContent_ToString_DoesNotPrintEntrySecrets` e `VaultAuthenticationException_IsAFixedMessage`.

## Zeragem de chaves no Lock

`VaultSession` guarda a Vault Key num buffer **fixado** (`GC.AllocateArray<byte>(VaultFormat.KeySize, pinned: true)`), então o GC não a move nem copia. `LockCore()` — chamado por `Lock()` e por `Dispose()` — faz:

```csharp
CryptographicOperations.ZeroMemory(_vaultKey);
_content = null;
_file = null;
```

- **`Dispose` reusa o mesmo caminho** e só marca `_disposed`; `Lock` é idempotente e, depois do `Dispose`, `Lock`/`Dispose` não fazem nada e o resto lança `ObjectDisposedException`.
- **Uma trava privada (`System.Threading.Lock`) serializa todos os membros**, então um `Lock` vindo de outra thread espera a gravação em andamento e nunca deixa selar o corpo com a chave zerada. A **exceção documentada é o `Import`**: ele segura a trava só para conferir o estado e roda o Argon2id/abertura fora dela, porque o custo vem do arquivo importado; ele não usa a chave nem o conteúdo da sessão.
- **`Unlock` zera a chave no `finally` quando falha** e a sessão continua bloqueada.
- **Chamador da UI**: `EntryListViewModel.Lock` chama `Clipboard.ExpireNow()` e depois `_session.Dispose()`; o bloqueio automático executa o mesmo comando. `UnlockViewModel` descarta a sessão nova quando o desbloqueio falha.

Os demais donos de temporários também zeram o que alocam:

| Classe | O que zera |
|---|---|
| `Crypto/Argon2idKdf.cs` | a senha normalizada em NFC e o UTF-8, os dois em buffers fixados, num `finally`; a KEK de saída no caminho de erro |
| `Vault/KeySlot.cs` | a KEK em `stackalloc`, num `finally`, nos quatro caminhos; a Vault Key de saída quando a autenticação falha |
| `Vault/MasterPassword.cs` | a Vault Key aberta do slot atual |
| `Vault/RecoveryKit.cs` | os 20 bytes da chave do kit em `stackalloc` (gerar, abrir e redefinir) |
| `Vault/VaultBody.cs` e `Crypto/VaultBodyCipher.cs` | o texto claro e o texto cifrado |
| `Vault/VaultCsv.cs` | o `char[]` fixado do documento e os bytes recebidos, em `finally` |
| `Vault/VaultImport.cs` | o array fixado do arquivo quando a leitura falha; o chamador zera depois de consumir |
| `Vault/VaultSession.cs` | `Create` (Vault Key), `VerifyPassword` (scratch), `Export` (chave de exportação) e `Import` (chave de importação) |
| `Passwords/Passphrase.cs` e `Passwords/PasswordGenerator.cs` | os índices do sorteio e o array rejeitado |

Testes que pinam cada afirmação: `VaultSessionTests.Lock_ZeroesKeyAndLocks`, `Lock_EveryOperation_ThrowsInvalidOperationException`, `Lock_DuringSaveLoop_FileAlwaysUnlocksAndDecrypts`, `Dispose_ZeroesKeyAndLaterCallsThrowObjectDisposedException` e `Unlock_TamperedBody_ThrowsZeroesKeyAndStaysLocked`; `KeySlotTests.Unwrap_Inputs_AreNotModified` e `Create_Inputs_AreNotModified`; nos ViewModels, o `Assert.All(chars, c => Assert.Equal('\0', c))` de `UnlockViewModelTests`, `CreateProfileViewModelTests`, `ExportViewModelTests`, `CsvExportViewModelTests`, `ImportViewModelTests`, `SettingsViewModelTests`, `RecoveryKitViewModelTests` e `NewRecoveryKitViewModelTests`.

**O limite que não dá para zerar**: as `string` gerenciadas do `VaultContent`, do rascunho, da senha mostrada e da área de transferência. `Lock` só solta as referências; o GC decide quando o heap é reusado. É o limite aceito da AGENTS §3.5, revisto um a um na seção seguinte.

## Strings gerenciadas e limites aceitos

Os sete itens que a arquitetura deixou "revisar na W-32" (§13), com o veredito desta revisão:

| Item (architecture.md §13) | Veredito | Motivo e onde está documentado |
|---|---|---|
| `PasswordBox` só expõe `string` | **Revisado e mantido** | O WinUI não tem API que entregue `char[]`; a cópia gerenciada criada antes de virar `char[]` fica no heap até o GC. §9.2 e §6; a página de criação tem o mesmo campo. |
| Tela de Desbloqueio capturável | **Revisado e corrigido** | A página mostra a senha mestra com o botão de revelar; agora não pode ser capturada. Achado e correção na seção seguinte. |
| Strings da W-16 (frase sugerida e texto do kit) | **Revisado e mantido** | Um `TextBlock` precisa de `string` para mostrar a frase e o kit; os `char[]` que o `Core` devolve são zerados pelos ViewModels, mas a `string` do `TextBlock` e a dos dois `PasswordBox` ficam no heap. §6, §5.1 e §9.5. |
| Rascunho e senha mostrada da W-20 | **Revisado e mantido** | O detalhe é um editor: o rascunho e o `DetailPasswordText` são `string` por desenho (mostrar e editar exigem texto). §9.8. |
| Senha gerada da W-21 | **Revisado e mantido** | O gerador devolve `char[]`, mas o resultado é gravado no rascunho como `string`, como a senha digitada. §9.9. |
| Senha copiada da W-22 | **Revisado e mantido** | O `ClipboardGuard` guarda a `string` copiada só até a cópia ser resolvida, no máximo 30 s; o rascunho já a guardava. §9.10. |
| Prévia da importação da W-25 | **Revisado e mantido** | As `string` das entradas importadas ficam no heap até Confirmar, Voltar ou o bloqueio; a prévia (`ImportPreviewRow`) só tem título, usuário e URL, nunca a senha. §9.13 e §6. |

Nenhuma dessas `string` pode ir para log nem para exceção — é a regra da AGENTS §3.5 e o que as guardas da W-32 verificam. São limites aceitos para a 1.0, não dívidas escondidas: cada um está declarado na arquitetura, e a proibição de logar segredos é a do modelo de ameaças.

## Achados e correções

**Achado 1 (corrigido nesta história): a página de Desbloqueio era a única com a senha mestra ainda capturável.** A W-14/W-17 mantinham a página capturável porque excluí-la quebraria os controles de pixels do teste de tema e do teste de proteção; a página de criação, com o mesmo campo e o mesmo botão de revelar, já estava excluída. A inconsistência era real: uma captura ou uma gravação podia mostrar a senha que abre o cofre.

Correção (W-32), antes → depois:

- `UnlockViewModel` **não sobrescreve mais** `AllowsScreenCapture`; herda o `false` fail-closed de `PageViewModel`, e a página passa a `WDA_EXCLUDEFROMCAPTURE`. A página de Perfis continua a única capturável.
- `ScreenCaptureTests`: o teste virou `AllowsScreenCapture_ProfilesIsTrueAndUnlockIsFalse`, e `Flow_CaptureIsAllowedOnlyOnTheProfilesPage` espera a sequência com as duas entradas de Desbloqueio em `false`.
- `WindowProtectionE2ETests` virou `Window_IsCapturableOnlyOnTheProfilesPage`: o Desbloqueio agora confere `WDA_EXCLUDEFROMCAPTURE`, inclusive depois de uma senha errada; o **controle de pixels** (≥ 5 %) passou para a página de Perfis, medido antes de abrir o perfil e de novo depois de Voltar do Desbloqueio (a pilha depois do bloqueio é `[Perfis, Desbloqueio]`).
- `ThemeE2ETests` não mede mais os pixels da página de Desbloqueio: os dois temas continuam medidos em Perfis e na barra de título.
- A mudança de afinidade obrigou a ajustar a expectativa de **`AutoLockE2ETests`, `SettingsE2ETests`, `CsvExportE2ETests`, `ExportE2ETests` e `ImportE2ETests`**: depois de um bloqueio a página de cima é o Desbloqueio, então a janela **continua** excluída (`WDA_EXCLUDEFROMCAPTURE`); ela só volta a `WDA_NONE` quando Voltar chega a Perfis. Os testes passaram a esperar isso e continuam afirmando que nenhum valor do cofre fica na tela.

**Nenhum outro achado.** A varredura não encontrou API de log, `SecureString` nem `ToString()` novo; a zeragem dos buffers de bytes e de `char[]` está completa nos pontos revistos; as exceções não carregam segredo nem conteúdo. O que sobra não é inconsistência, é limite já declarado: as `string` gerenciadas da seção anterior e a cobertura que a exclusão da página de Desbloqueio tira.

**Cobertura que a correção tira (declarada, não escondida):** a página de Desbloqueio deixa de ser medida pelo teste de tema/contraste em pixels. A guarda estática de acessibilidade continua classificando a página (nomes, foco, tema em XAML/C#), e a página de Perfis continua com os dois temas medidos; mas não há mais medição de contraste em pixels na tela que mostra a senha mestra. O teste manual de captura (item 4 da validação) é o que fecha a verificação da W-32, e **não** foi executado por esta revisão.

## Veredito

As dez linhas do modelo de ameaças têm implementação e teste identificáveis. Nove estão cobertas: a cópia do arquivo e a adulteração pelo Argon2id e pelo AEAD; o aparelho desbloqueado pelo bloqueio automático e pelo atraso do desbloqueio; a captura de tela pela afinidade fail-closed, agora inclusive no Desbloqueio; a área de transferência pela limpeza em 30 s e pelos marcadores; a importação hostil pelos limites e pela prévia sem senha; o `settings.json` pelos valores limitados; a rede e a telemetria pelo manifesto sem capability e pelo grafo de pacotes; os logs pela ausência de API e pelos dois `ToString()` auditados. A linha da cópia antiga **não protege por desenho** e continua assim: a Vault Key é fixa por perfil. Ficam para a W-33 a prova de zero conexões em execução e para a W-34 o roteiro manual — **nenhuma das duas foi rodada** — e os riscos residuais aceitos são as `string` gerenciadas das seções 4 e 5 (incluindo a perda da medição de pixels da página de Desbloqueio). O único defeito encontrado virou correção nesta história; as guardas da W-32 impedem que um log ou um `ToString()` novo entre sem revisão.

## Como reproduzir

Varreduras, da raiz do repositório (a primeira lista é a da validação da história; a segunda acrescenta `TraceSource` e `Debugger.`):

```
rg -n "Console\.|Debug\.Write|Debug\.Print|Trace\.|ILogger|EventLog|SecureString" windows/src
rg -n "Console\.|Debug\.Write|Debug\.Print|Trace\.|TraceSource|ILogger|EventLog|Debugger\.|SecureString" windows/src
rg -n "override string ToString" windows/src
```

Resultado esperado: nenhum casamento nas duas primeiras e exatamente dois arquivos na terceira (`VaultEntry.cs` e `EntryDetailViewModel.cs`).

Testes, a partir de `windows/`:

```
dotnet test --project tests/PasswordManager.Core.Tests/PasswordManager.Core.Tests.csproj --filter-class PasswordManager.Core.Tests.Vault.VaultSessionTests
dotnet test --project tests/PasswordManager.ViewModels.Tests/PasswordManager.ViewModels.Tests.csproj --filter-class PasswordManager.ViewModels.Tests.ScreenCaptureTests
dotnet test --project tests/PasswordManager.App.E2E.Tests/PasswordManager.App.E2E.Tests.csproj --filter-class PasswordManager.App.E2E.Tests.WindowProtectionE2ETests
dotnet test --project tests/PasswordManager.App.E2E.Tests/PasswordManager.App.E2E.Tests.csproj --filter-class PasswordManager.App.E2E.Tests.ThemeE2ETests
dotnet test --project tests/PasswordManager.App.E2E.Tests/PasswordManager.App.E2E.Tests.csproj --filter-class PasswordManager.App.E2E.Tests.Security.SecretGuardTests
dotnet test --project tests/PasswordManager.App.E2E.Tests/PasswordManager.App.E2E.Tests.csproj --filter-class PasswordManager.App.E2E.Tests.Security.SecretGuardTests -c Release
dotnet test --project tests/PasswordManager.Core.Tests/PasswordManager.Core.Tests.csproj --filter-class PasswordManager.Core.Tests.Security.SecretHygieneTests
```

Os E2E precisam de desktop interativo e só rodam em Debug (a guarda `SecretGuardTests` roda nos dois). A revisão completa é `cd windows; dotnet test --solution PasswordManager.slnx`.

Conferência manual da W-32 (item 4 da validação, **não executada nesta revisão**): no app Debug, abrir um perfil, apertar o botão de revelar do campo de senha do Desbloqueio e capturar a janela com a Ferramenta de Captura — a área da janela mostra o que está atrás dela; a mesma captura na página de Perfis mostra a página (o controle).
