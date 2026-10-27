# Vetores de teste

Arquivos usados pelos testes das **duas** plataformas para garantir que um cofre gerado no Windows abre no Android e vice-versa. O formato está em [`docs/vault-format.md`](../../docs/vault-format.md).

Cada vetor é um par:

- `<nome>.pmvault`: cofre v1 com dados fictícios;
- `<nome>.json`: manifesto com a senha, a Vault Key, os slots esperados e o corpo decifrado.

Os arquivos commitados são a verdade. Os testes normais só leem esses arquivos e nunca os geram de novo.

## Vetores

| Vetor | Conteúdo | KDF dos slots |
|---|---|---|
| `empty` | Cofre sem entradas, só o slot de senha, senha ASCII. | 1024 KiB / 2 / 1 |
| `entries-unicode` | 4 entradas: acentos pt-BR; emoji, inclusive a sequência ZWJ `👨‍👩‍👧`, no título, na senha e na pasta; CJK, hebraico e árabe; notas com `\n`, `\t`, `"` e `\`; uma string em NFD nas notas (`Cafe` + U+0301); campos opcionais vazios; `favorite` verdadeiro e falso; datas com milissegundos e `createdAt != updatedAt`. Senha em NFC, não ASCII (`í ç ã ñ` e um emoji fora do BMP). | 1024 KiB / 2 / 1 |
| `recovery-kit` | Criado pelo caminho real de novo perfil (`VaultSession.Create`), com slot de senha e slot do Kit de Recuperação, e depois gravado com 2 entradas (uma com emoji). Prova que os parâmetros de produção funcionam nas duas plataformas. | 65536 KiB / 3 / 1 (padrão) |
| `export` | Exportação cifrada do `recovery-kit`: Vault Key nova, exatamente um slot tipo 1 (senha de exportação), nenhum slot tipo 2 e o mesmo conteúdo. | 1024 KiB / 2 / 1 |

O custo 1024 KiB / 2 / 1 só existe para os testes rodarem rápido. Ele está dentro dos limites de leitura do formato, mas nunca é usado por um escritor de verdade.

## Manifesto (`<nome>.json`)

UTF-8 sem BOM, camelCase, quebras de linha LF. Hex sempre em minúsculas. Campos opcionais ficam de fora quando não se aplicam.

| Campo | Tipo | Conteúdo |
|---|---|---|
| `description` | string | Texto curto em pt-BR. |
| `file` | string | `"<nome>.pmvault"`. |
| `formatVersion` | número | `formatVersion` do preâmbulo (`1`). |
| `password` | string | Segredo do slot tipo 1: a senha mestra, ou a senha de exportação. |
| `vaultKeyHex` | string | Os 32 bytes da Vault Key que o slot tipo 1 desembrulha. |
| `recoveryKit` | string, opcional | Texto do Kit de Recuperação (8 grupos de 4 símbolos). Presente só quando há slot tipo 2. |
| `recoveryKeyHex` | string, opcional | Os 20 bytes que o texto do kit decodifica. Presente só com `recoveryKit`. |
| `sourceVector` | string, opcional | Só em exportações: o vetor de onde o conteúdo saiu. |
| `slots` | array | `[{type, kdfMemoryKiB, kdfIterations, kdfParallelism}]`, na ordem do arquivo. |
| `bodyOffset` | número | `B`, o offset do `bodyNonce` (= tamanho do cabeçalho sem o nonce): `7 + 101·n` com `n` slots dos tipos 1 e 2, ou seja 108 com 1 slot e 209 com 2. |
| `content` | objeto | O JSON do corpo decifrado, `{"entries":[…]}`. |

O gerador preenche `vaultKeyHex` desembrulhando os bytes finais do arquivo e `content` decifrando o corpo deles, então o manifesto descreve exatamente o que está no arquivo.

## Como um leitor usa os vetores

Para cada manifesto em `spec/test-vectors/*.json` (o leitor deve listar a pasta, para que um vetor novo seja testado sem mudar código):

1. Conferir o cabeçalho com `slots`, `bodyOffset` e `formatVersion` (não precisa de Argon2id).
2. Desbloquear com `password` e comparar a Vault Key com `vaultKeyHex`.
3. Comparar o corpo decifrado com `content` **semanticamente**: JSON já interpretado, strings comparadas por ordinal (código a código, sem normalizar). Nunca comparar bytes do JSON: a ordem dos campos, os espaços e os escapes (`\uD83D…` ou o caractere direto) podem mudar entre bibliotecas.
4. Se houver `recoveryKit`: decodificar e comparar com `recoveryKeyHex`, desbloquear pelo slot tipo 2 e comparar com `vaultKeyHex`. Sem `recoveryKit`, o desbloqueio por kit tem de falhar.
5. No `entries-unicode`: conferir também que a forma **NFD** da senha desbloqueia o cofre, porque a entrada do KDF é `UTF-8(NFC(senha))`.
6. Senha errada tem de falhar como erro de autenticação.

Os testes abrem uma **cópia** em pasta temporária, nunca o arquivo dentro de `spec/`. Os testes do `core` do Android têm de abrir estes mesmos arquivos (e não cópias regeneradas).

## Gerar de novo

O gerador é um teste explícito do Windows, que não roda na suíte normal. A partir de `windows/`:

```
dotnet test --project tests/PasswordManager.Core.Tests/PasswordManager.Core.Tests.csproj --filter-class "PasswordManager.Core.Tests.TestVectors.TestVectorGenerator" --explicit only
```

- Salts, nonces e Vault Keys vêm do CSPRNG, então cada execução muda todos os bytes.
- O gerador **não sobrescreve**: se qualquer um dos 8 arquivos existir, ele falha com "already exists" sem tocar em nada. Para gerar de novo, apague os 8 arquivos antes.
- Regenerar só é permitido antes do congelamento da v1. Depois dele, os vetores existentes nunca mudam; uma versão nova do formato ganha vetores novos, e os antigos ficam enquanto a versão antiga tiver de ser lida.

## Regras

- Somente dados fictícios. Nunca coloque aqui um cofre ou senha real.
- Mudou o formato (`docs/vault-format.md`)? Gere vetores novos e mantenha os antigos enquanto a versão antiga tiver de ser lida.
