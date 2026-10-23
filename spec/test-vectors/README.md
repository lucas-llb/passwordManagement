# Vetores de teste

Arquivos usados pelos testes das **duas** plataformas para garantir que um cofre gerado no Windows abre no Android e vice-versa.

Cada vetor terá:

- `<nome>.pmvault` — cofre gerado com dados fictícios;
- `<nome>.json` — senha usada, parâmetros esperados do cabeçalho e o JSON do corpo esperado após decifrar.

Regras:

- Somente dados fictícios. Nunca coloque aqui um cofre ou senha real.
- Mudou o formato (`docs/vault-format.md`)? Gere novos vetores e mantenha os antigos enquanto a versão antiga tiver de ser lida.
