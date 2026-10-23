# Formato do cofre — v1 (rascunho)

> Fonte da verdade do formato. Enquanto não houver release, a v1 ainda pode mudar; depois da primeira release, qualquer mudança cria uma nova versão.
> Constantes no código: `windows/src/PasswordManager.Core/Vault/VaultFormat.cs` e `android/core/src/main/kotlin/com/passwordmanagement/core/vault/VaultFormat.kt`.

## Arquivos por perfil

| Arquivo | Conteúdo | Cifrado? |
|---|---|---|
| `profiles.json` | lista de perfis: `id` (UUID), `displayName`, `createdAt` | não |
| `<id>.pmvault` | cofre do perfil (formato abaixo) | sim |
| `<id>.pmvault.bak` | versão anterior do cofre | sim |
| `<id>.biometric` | (só Android) Vault Key envolvida pela chave do Keystore | sim |

## Layout binário de `<id>.pmvault`

Inteiros em little-endian.

| Offset | Tamanho | Campo | Valor v1 |
|---:|---:|---|---|
| 0 | 4 | magic | ASCII `PMVF` |
| 4 | 2 | formatVersion (uint16) | `1` |
| 6 | 1 | kdfAlgorithm (uint8) | `1` = Argon2id v1.3 |
| 7 | 4 | kdfMemoryKiB (uint32) | `65536` (64 MiB) |
| 11 | 4 | kdfIterations (uint32) | `3` |
| 15 | 1 | kdfParallelism (uint8) | `1` (libsodium só suporta 1) |
| 16 | 16 | salt | aleatório |
| 32 | 24 | wrapNonce | aleatório |
| 56 | 48 | wrappedVaultKey | Vault Key (32) + tag (16) |
| 104 | 24 | bodyNonce | aleatório, novo a cada gravação |
| 128 | n | bodyCiphertext | JSON UTF-8 + tag (16) |

## Criptografia

1. `KEK = Argon2id(senhaMestra UTF-8, salt, kdfMemoryKiB, kdfIterations)` → 32 bytes.
2. `wrappedVaultKey = XChaCha20-Poly1305(KEK, wrapNonce, VaultKey, AAD = bytes[0..32))`.
3. `bodyCiphertext = XChaCha20-Poly1305(VaultKey, bodyNonce, json, AAD = bytes[0..128))`.

Como o cabeçalho inteiro é AAD do corpo, qualquer byte alterado no cabeçalho faz a decifragem falhar. Senha errada é detectada pela tag inválida em (2); o app não guarda hash da senha.

Exportação cifrada usa o mesmo layout, com a senha de exportação no lugar da senha mestra e uma Vault Key nova.

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

## Em aberto

- Kit de Recuperação: onde guardar o segundo envelope da Vault Key (provável: segundo slot no cabeçalho, o que muda o layout → decidir antes da primeira release).
