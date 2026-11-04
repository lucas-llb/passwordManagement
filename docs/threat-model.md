# Modelo de ameaças (resumo)

## O que protegemos

As entradas do cofre (senhas, usuários, URLs, notas) e a senha mestra.

## Contra quem / o quê

| Ameaça | Proteção |
|---|---|
| Alguém copia o arquivo do cofre (backup, disco roubado, exportação vazada) | Argon2id + XChaCha20-Poly1305; nada legível sem a senha |
| Adulteração do arquivo | Cabeçalho e corpo autenticados (AEAD) |
| Pessoa com acesso ao aparelho desbloqueado | Bloqueio automático por inatividade e ao sair do app; biometria exige `CryptoObject` |
| Captura de tela / gravação / miniatura em "recentes" | `FLAG_SECURE` (Android), `WDA_EXCLUDEFROMCAPTURE` (Windows) |
| Senha copiada esquecida na área de transferência | Limpeza após 30 s; fora do histórico e do clipboard em nuvem |
| Arquivo de importação hostil (um `.pmvault` ou CSV malformado, gigante ou adulterado que o usuário escolhe) | Limites antes de alocar (32 MiB, 20.000 itens, 65.536 caracteres por campo, 64 colunas); CSV em UTF-8 estrito e recusado inteiro quando a estrutura quebra; um `.pmvault` só chega à prévia depois do Argon2id e da tag AEAD válida no slot e no corpo, e a senha errada e o arquivo adulterado têm uma só mensagem; nada é gravado antes de "Confirmar importação" (uma gravação atômica, com `.bak`), a prévia nunca mostra senha e o arquivo de origem nunca é alterado; o bloqueio não espera pelo custo de Argon2id que o arquivo escolheu. Sobra: um arquivo de custo máximo mantém a página ocupada por dezenas de segundos |
| Alguém edita o `settings.json` das preferências (tempo de inatividade e bloqueio ao minimizar, W-26) | O arquivo não tem nada de um cofre (nem senha, nem nome de perfil, nem caminho) e os valores são limitados a um conjunto (1, 2, 5, 10, 15, 30 ou 60 min; um valor fora dele volta ao padrão de 5 min, e um arquivo ruim vale como o padrão). Não é autenticado, então, no pior caso, quem escreve na pasta de cofres põe 60 min e desliga o bloqueio ao minimizar. Não existe "nunca bloquear", e o bloqueio por sessão do Windows não é configurável |
| Uma cópia antiga do `.pmvault` (backup ou arquivo copiado) com uma credencial antiga (senha mestra ou Kit de Recuperação já trocados, W-26) | **Não protege.** A Vault Key é fixa por perfil, então a cópia antiga ainda abre com a credencial que valia nela e entrega a mesma Vault Key que abre o arquivo atual. O app só refaz o `.bak` ao lado do cofre depois da troca; as cópias por fora não são alcançadas, e a tela diz isso (`SettingsPasswordNote` e `SettingsKitNote`). Quem precisa que uma cópia antiga deixe de abrir tem de apagá-la |
| Vazamento por rede ou telemetria | Sem permissão/capability de internet; sem dependências de rede |
| Vazamento por logs | Proibido logar segredos; `toString()` das entradas não mostra segredos |

## Fora do alcance

- Malware com privilégios no dispositivo enquanto o cofre está aberto (keylogger, leitura de memória).
- Senha mestra fraca escolhida pelo usuário (mitigado só pelo medidor de força e pelo custo do Argon2id).
- Aparelho com root/jailbreak.
