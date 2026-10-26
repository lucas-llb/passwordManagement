# Formato do cofre — v1

> Fonte da verdade do formato. O layout foi fechado na W-01 (decisão D1 do épico: Kit de Recuperação na 1.0, como tabela de slots). Ele fica congelado de vez quando os dois `core` (Windows e Android) passarem nos vetores da W-10 e na primeira release; depois disso, qualquer mudança é a v2.
> Constantes no código: `windows/src/PasswordManager.Core/Vault/VaultFormat.cs` e `android/core/src/main/kotlin/com/passwordmanagement/core/vault/VaultFormat.kt` (tabela na seção "Constantes").

## Arquivos por perfil

| Arquivo | Conteúdo | Cifrado? |
|---|---|---|
| `profiles.json` | lista de perfis `{"profiles":[…]}`: `id` (UUID), `displayName`, `createdAt`. Local ao dispositivo, nunca exportado, sem `.bak` (ver "profiles.json") | não |
| `profiles.json.tmp` | temporário da gravação atômica da lista; só sobra após uma queda e é sobrescrito na próxima gravação | não |
| `<id>.pmvault` | cofre do perfil (formato abaixo) | sim |
| `<id>.pmvault.bak` | versão anterior do cofre | sim |
| `<id>.pmvault.tmp` | temporário da gravação atômica; só sobra após uma queda e é sobrescrito na próxima gravação | sim |
| `<id>.biometric` | (só Android) Vault Key envolvida pela chave do Keystore. **Fora do formato portátil**: formato privado do app, nunca exportado e excluído do backup (ver "Desbloqueio vinculado ao dispositivo") | sim |

## profiles.json

Lista dos perfis do dispositivo, na mesma pasta dos cofres. Fica em claro (ver "Metadados em claro"): só tem o nome de exibição, que a tela de seleção precisa mostrar antes de desbloquear. Não faz parte do formato portátil: nunca é exportada e não entra nos vetores de teste.

- Pasta: `%LOCALAPPDATA%\PasswordManagement\vaults\` no Windows sem pacote, `<LocalFolder>\vaults\` quando empacotado (MSIX). O Android seguirá o mesmo formato em `filesDir`.
- Formato (UTF-8 sem BOM, JSON compacto, campos em camelCase):

```json
{
  "profiles": [
    {
      "id": "0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b",
      "displayName": "Trabalho",
      "createdAt": "2026-01-01T12:00:00.000Z"
    }
  ]
}
```

- A ordem do array é a ordem de criação.
- `id` é UUID minúsculo com hífens, nunca todo zero e sem repetição. Os nomes de arquivo (`<id>.pmvault`, `.bak`, `.tmp`) vêm só do `id`, nunca do `displayName`.
- `createdAt` é ISO-8601 UTC com milissegundos e sufixo `Z`, como no corpo do cofre.
- Regras do `displayName`, na ordem em que são conferidas:
  1. Sem caractere de controle e sem surrogate solto.
  2. Normalizado em Unicode NFC e sem espaços nas pontas.
  3. Não vazio.
  4. No máximo 64 unidades UTF-16.
  5. Único entre os perfis, sem diferenciar maiúsculas de minúsculas (comparação ordinal).
- Leitores ignoram campos desconhecidos. Arquivo ausente (ou pasta ausente) significa nenhum perfil. Arquivo corrompido (JSON inválido, elemento nulo, `id` inválido ou repetido, `displayName` vazio, campo obrigatório ausente) é erro e nunca é sobrescrito automaticamente.
- Gravação atômica como a do cofre (`profiles.json.tmp`, flush, renomear por cima), mas **sem `.bak`**: um `.bak` guardaria os nomes de perfis já apagados.
- Ordem ao criar: grava o `<id>.pmvault` primeiro e depois a lista; se a lista falhar, o cofre novo é removido (melhor esforço), então todo perfil listado tem cofre em disco.
- Ordem ao apagar: tira o perfil da lista primeiro e depois remove `<id>.pmvault`, `.bak` e `.tmp`. Se um arquivo não puder ser removido, o erro sobe e repetir a operação limpa o que sobrou. Cofres órfãos (sem entrada na lista) nunca são apagados automaticamente.
- Sem trava entre processos: quem chama deve serializar as operações.

## Layout binário de `<id>.pmvault`

- Todos os inteiros são little-endian.
- O arquivo é: preâmbulo, depois os slots, depois o corpo.
- `B` = offset de `bodyNonce` = 7 + total de bytes de todos os registros de slot. Quando todos os slots são do tipo 1 ou 2, `B = 7 + 101·n`.

| Offset | Tamanho | Campo | Valor v1 |
|---:|---:|---|---|
| 0 | 4 | magic | ASCII `PMVF` = `50 4D 56 46` |
| 4 | 2 | formatVersion (u16) | `1` |
| 6 | 1 | slotCount (u8) | 1..8 (os escritores v1 gravam 1 ou 2) |
| 7 | variável | registros de slot | em ordem crescente de `slotType` |
| B | 24 | bodyNonce | aleatório, novo a cada gravação |
| B+24 | resto (≥ 16) | bodyCiphertext | ciphertext ‖ tag (16) |

### Registro de slot

Os offsets abaixo são relativos ao início do slot.

| Offset | Tamanho | Campo | Valor v1 |
|---:|---:|---|---|
| 0 | 1 | slotType (u8) | `1` = senha, `2` = Kit de Recuperação. `0` é inválido. `3..255` são reservados. |
| 1 | 2 | slotLength (u16) | bytes que vêm depois deste campo; deve ser `98` nos tipos 1 e 2 |
| 3 | 1 | kdfAlgorithm (u8) | `1` = Argon2id v1.3 |
| 4 | 4 | kdfMemoryKiB (u32) | padrão `65536` |
| 8 | 4 | kdfIterations (u32) | padrão `3` |
| 12 | 1 | kdfParallelism (u8) | `1` |
| 13 | 16 | salt | aleatório, um por slot |
| 29 | 24 | wrapNonce | aleatório, novo a cada envelopamento |
| 53 | 48 | wrappedVaultKey | ciphertext (32) ‖ tag (16) |
| 101 | — | fim | |

Tamanhos resultantes:

- 1 slot: cabeçalho de 132 bytes; o corpo começa no offset 132.
- 2 slots: cabeçalho de 233 bytes.
- Arquivo mínimo: 148 bytes (1 slot + bodyNonce + tag).

## Criptografia

- Entrada da senha: `P = UTF-8(NFC(senha))`, sem `trim` e sem terminador.
- Entrada do Kit de Recuperação: `R` = os 20 bytes decodificados do texto do kit (nunca o texto em si).
- `KEK = crypto_pwhash(outlen 32, P ou R, salt, opslimit = kdfIterations, memlimit = kdfMemoryKiB × 1024 bytes, ARGON2ID13)`.
  - NSec recebe `MemorySize = kdfMemoryKiB` (em KiB).
  - lazysodium recebe `memLimit` em bytes (`kdfMemoryKiB × 1024`).
- `AAD_wrap = file[0..6) ‖ slot[0..29)` (35 bytes: magic, versão, slotType, slotLength, parâmetros do KDF e salt).
  - Ele exclui, de propósito, `slotCount`, os outros slots, `wrapNonce` e `wrappedVaultKey`.
  - Assim cada slot pode ser trocado sem os segredos dos outros: trocar a senha não exige o kit, e gerar um kit novo não exige a senha.
- `wrappedVaultKey = crypto_aead_xchacha20poly1305_ietf_encrypt(VaultKey, AAD_wrap, wrapNonce, KEK)` (modo combinado da libsodium: ciphertext ‖ tag).
- `AAD_body = file[0 .. B+24)`: o cabeçalho inteiro, incluindo todos os slots (os desconhecidos também) e o `bodyNonce`.
- `bodyCiphertext = XChaCha20-Poly1305(VaultKey, bodyNonce, JSON UTF-8, AAD_body)`.
- Senha ou kit errado aparece como falha AEAD no desembrulho daquele slot. Nenhum hash da senha é guardado.

## Validação na leitura

Nesta ordem. Qualquer falha é um erro de formato tipado e não devolve dado parcial.

1. Tamanho do arquivo ≥ 148.
2. O magic confere.
3. `formatVersion == 1`; qualquer outro valor significa "versão não suportada".
4. `slotCount` está em 1..8.
5. Cada slot:
   - Restam ao menos 3 bytes e `slotLength` cabe no arquivo.
   - Tipo 0 é erro.
   - Tipos 1 e 2 exigem `slotLength == 98`, `kdfAlgorithm == 1`, `kdfParallelism == 1`, `kdfMemoryKiB` em [8, 1048576] e `kdfIterations` em [1, 32]. Esses limites são um teto contra DoS e coincidem com os mínimos do NSec/libsodium.
   - Tipos 3..255 são pulados e guardados como bytes brutos.
   - Nenhum `slotType` pode se repetir. É obrigatório haver exatamente um slot tipo 1.
6. Depois dos slots restam ao menos 24 + 16 bytes.

Desbloqueio: a senha tenta só o slot tipo 1; o kit tenta só o slot tipo 2. Nunca percorrer todos os slots, porque cada tentativa custa um Argon2id.

## Regras de escrita

- A Vault Key tem 32 bytes do CSPRNG, é criada uma vez por perfil e nunca muda durante a vida do perfil.
- Todo envelopamento usa `salt` e `wrapNonce` novos, do CSPRNG.
- Toda gravação usa um `bodyNonce` novo.
- Parâmetros padrão na escrita: 64 MiB / 3 / 1 para **os dois** tipos de slot. Valores menores só são permitidos em testes.
- Os slots são gravados em ordem crescente de `slotType`. Slots desconhecidos são preservados byte a byte.
- Troca da senha mestra:
  - Substitui só o slot tipo 1.
  - Os bytes do slot tipo 2 ficam idênticos.
  - O corpo é re-selado com a mesma Vault Key e um `bodyNonce` novo, porque o cabeçalho é AAD.
  - A Vault Key e as entradas não são re-cifradas com outra chave.
- Kit de Recuperação:
  - Gerar de novo substitui o slot tipo 2, e o kit antigo deixa de funcionar.
  - Remover o kit tira o slot, deixando `slotCount = 1`.
- Arquivos `.pmvault` exportados usam o mesmo layout, uma Vault Key nova, exatamente um slot tipo 1 (a senha de exportação) e nenhum slot tipo 2.
- Gravação atômica (igual nas duas plataformas; `tmp` e `bak` ficam na mesma pasta do `<id>.pmvault`, então a renomeação nunca cruza volumes):
  1. Validar os bytes completos como na leitura. Bytes inválidos falham antes de tocar o disco.
  2. Criar ou truncar `<id>.pmvault.tmp` (um temporário antigo, de uma queda, é sobrescrito), gravar os bytes e forçar a escrita no disco (flush/fsync).
  3. Se `<id>.pmvault` existe, copiá-lo por cima de `<id>.pmvault.bak`. Na primeira gravação não se cria `.bak`.
  4. Renomear `<id>.pmvault.tmp` por cima de `<id>.pmvault`, de forma atômica.
  5. Ao final, com ou sem erro, apagar `<id>.pmvault.tmp` se ainda existir (melhor esforço). A gravação nunca cria pastas.
  - Resultado: o `<id>.pmvault` nunca deixa de existir; em falha, ele fica como estava.

## Kit de Recuperação

Texto canônico, idêntico nas duas plataformas.

- 20 bytes (160 bits) do CSPRNG.
- Codificação: base32 Crockford com o alfabeto `0123456789ABCDEFGHJKMNPQRSTVWXYZ`, bits do mais significativo para o menos (MSB-first, como na RFC 4648), 32 símbolos, sem padding, em maiúsculas.
- Exibição: 8 grupos de 4 símbolos separados por `-` (39 caracteres).
- Decodificação:
  - Remover `-` e espaços e converter para maiúsculas.
  - Mapear `O` para `0`, e `I` e `L` para `1`.
  - Qualquer outro caractere (inclusive `U`), ou uma quantidade diferente de 32 símbolos, significa "kit inválido". Essa checagem acontece antes do Argon2id.
- Por que Argon2id para uma chave aleatória: é o único KDF da lista do AGENTS §3.2 (HKDF e BLAKE2b não estão nela), e um segredo de 160 bits precisa gerar uma KEK de 256 bits. O custo de ~1 s é aceitável para uma operação rara.

## Desbloqueio vinculado ao dispositivo

Estes desbloqueios não são slots do cabeçalho.

- `<id>.biometric` existe só no Android. Uma chave AES-256-GCM do Keystore envolve a **mesma** Vault Key, em um arquivo local separado, como exige o AGENTS §4.1.
- O formato desse arquivo é privado do app Android. Ele nunca é exportado e fica excluído do backup.
- Ele continua válido depois de trocas de senha, porque a Vault Key não muda.
- Um futuro desbloqueio por Windows Hello segue o mesmo modelo.
- Por que não são slots do cabeçalho:
  - Chaves de dispositivo não sobrevivem a cópia, backup ou exportação.
  - Colocá-las no cabeçalho vazaria dados do dispositivo para as exportações.
  - Toda mudança de cadastro reescreveria o cofre.
- A tabela de slots continua sendo o ponto de extensão para futuros desbloqueios *portáteis*.

## Metadados em claro

Ficam em claro no `.pmvault`: a versão do formato, a quantidade e os tipos dos slots (o que revela se existe um kit) e os parâmetros do KDF. Todo o resto é cifrado.

## Constantes

Mesmos valores nas duas plataformas. Offsets, tamanhos e contagens são `int`/`Int`; tipos de slot são `byte`/`Int`; memória e iterações do KDF são `uint`/`Long`. `Version` é `ushort` em C# e `Int` em Kotlin. `Magic`/`MAGIC` devolve os bytes de `PMVF` (em Kotlin, um array novo a cada acesso); todas as demais linhas são `public const` em C# e `const val` em Kotlin.

| C# | Kotlin | Valor |
|---|---|---|
| `Magic` | `MAGIC` | `"PMVF"` (`50 4D 56 46`) |
| `MagicSize` | `MAGIC_SIZE` | 4 |
| `Version` (ushort) | `VERSION` | 1 |
| `VersionOffset` | `VERSION_OFFSET` | 4 |
| `SlotCountOffset` | `SLOT_COUNT_OFFSET` | 6 |
| `FirstSlotOffset` | `FIRST_SLOT_OFFSET` | 7 |
| `PreambleSize` | `PREAMBLE_SIZE` | 6 |
| `MinSlotCount` | `MIN_SLOT_COUNT` | 1 |
| `MaxSlotCount` | `MAX_SLOT_COUNT` | 8 |
| `SlotTypePassword` | `SLOT_TYPE_PASSWORD` | 1 |
| `SlotTypeRecoveryKit` | `SLOT_TYPE_RECOVERY_KIT` | 2 |
| `SlotHeaderSize` | `SLOT_HEADER_SIZE` | 3 |
| `SlotTypeOffset` | `SLOT_TYPE_OFFSET` | 0 |
| `SlotLengthOffset` | `SLOT_LENGTH_OFFSET` | 1 |
| `SlotKdfAlgorithmOffset` | `SLOT_KDF_ALGORITHM_OFFSET` | 3 |
| `SlotKdfMemoryOffset` | `SLOT_KDF_MEMORY_OFFSET` | 4 |
| `SlotKdfIterationsOffset` | `SLOT_KDF_ITERATIONS_OFFSET` | 8 |
| `SlotKdfParallelismOffset` | `SLOT_KDF_PARALLELISM_OFFSET` | 12 |
| `SlotSaltOffset` | `SLOT_SALT_OFFSET` | 13 |
| `SlotWrapNonceOffset` | `SLOT_WRAP_NONCE_OFFSET` | 29 |
| `SlotWrappedKeyOffset` | `SLOT_WRAPPED_KEY_OFFSET` | 53 |
| `KdfSlotBodySize` | `KDF_SLOT_BODY_SIZE` | 98 |
| `KdfSlotSize` | `KDF_SLOT_SIZE` | 101 |
| `WrapAadSize` | `WRAP_AAD_SIZE` | 35 |
| `KdfArgon2id` | `KDF_ARGON2ID` | 1 |
| `DefaultKdfMemoryKiB` | `DEFAULT_KDF_MEMORY_KIB` | 65536 |
| `DefaultKdfIterations` | `DEFAULT_KDF_ITERATIONS` | 3 |
| `DefaultKdfParallelism` | `DEFAULT_KDF_PARALLELISM` | 1 |
| `MinKdfMemoryKiB` | `MIN_KDF_MEMORY_KIB` | 8 |
| `MaxKdfMemoryKiB` | `MAX_KDF_MEMORY_KIB` | 1048576 |
| `MinKdfIterations` | `MIN_KDF_ITERATIONS` | 1 |
| `MaxKdfIterations` | `MAX_KDF_ITERATIONS` | 32 |
| `SaltSize` | `SALT_SIZE` | 16 |
| `KeySize` | `KEY_SIZE` | 32 |
| `NonceSize` | `NONCE_SIZE` | 24 |
| `TagSize` | `TAG_SIZE` | 16 |
| `WrappedKeySize` | `WRAPPED_KEY_SIZE` | 48 |
| `MinFileSize` | `MIN_FILE_SIZE` | 148 |
| `RecoveryKeySize` | `RECOVERY_KEY_SIZE` | 20 |
| `RecoveryKeyAlphabet` | `RECOVERY_KEY_ALPHABET` | `"0123456789ABCDEFGHJKMNPQRSTVWXYZ"` |
| `RecoveryKeyGroupLength` | `RECOVERY_KEY_GROUP_LENGTH` | 4 |
| `RecoveryKeyGroupCount` | `RECOVERY_KEY_GROUP_COUNT` | 8 |
| `RecoveryKeySeparator` (char) | `RECOVERY_KEY_SEPARATOR` (Char) | `'-'` |
| `FileExtension` | `FILE_EXTENSION` | `".pmvault"` |

## Corpo (JSON)

```json
{
  "entries": [
    {
      "id": "0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b",
      "title": "Exemplo",
      "username": "user@example.com",
      "password": "...",
      "url": "https://example.com",
      "notes": "",
      "folder": "",
      "favorite": false,
      "createdAt": "2026-01-01T12:00:00.000Z",
      "updatedAt": "2026-01-01T12:00:00.000Z"
    }
  ]
}
```

- Campos em camelCase; `id` é UUID minúsculo com hífens.
- Datas em ISO-8601 UTC com milissegundos e sufixo `Z`.
- Leitores ignoram campos desconhecidos (compatibilidade para frente).
