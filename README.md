# Flowa — envio de ordens com FIX 4.4

Duas aplicações em C# que conversam por FIX: o OrderGenerator manda ordens de compra e venda montadas
numa tela, e o OrderAccumulator aceita ou rejeita cada uma conforme o limite de exposição por ativo.

## Tecnologias

- C# no .NET 10 (ASP.NET Core) para as duas aplicações
- QuickFIX/n 1.14.1 (`QuickFIXn.Core` e `QuickFIXn.FIX44`) para o protocolo FIX 4.4
- PostgreSQL 17, acessado com Npgsql e Dapper
- React 19, TypeScript e Vite na tela
- xUnit e Testcontainers nos testes do .NET; Vitest e Playwright nos testes da tela
- Docker Compose para subir tudo junto
- GitHub Actions, que compila e testa as regras de campo (`Flowa.Shared`) em cada PR

## Como rodar com Docker

Precisa só do Docker com o Compose. Na raiz do repositório:

```bash
docker compose up
```

Isso constrói as imagens e sobe o PostgreSQL, o OrderAccumulator e o OrderGenerator. A página fica em
http://localhost:8080 (só nesta máquina). `Ctrl+C` para; `docker compose down -v` apaga também o banco.

A senha do PostgreSQL vem da variável `POSTGRES_PASSWORD`. Sem ela, o compose usa `flowa_dev`, que é um
valor só de desenvolvimento local: o banco não sai da rede interna do compose. Se a 8080 estiver
ocupada, use `FLOWA_HTTP_PORT=9080 docker compose up`.

## Como rodar sem Docker

Precisa do .NET 10 SDK, do Node.js 22 e de um PostgreSQL 17 com um banco `flowa` e um usuário `flowa`.
As tabelas são criadas pelo OrderAccumulator quando ele sobe. Os comandos abaixo são para bash (no
Windows, o Git Bash serve) e usam as portas do contrato: 8080, 8081 e 9876.

Primeiro a tela, que é gravada dentro do OrderGenerator:

```bash
cd frontend
npm ci
npm run build
cd ..
```

Depois o OrderAccumulator, num terminal (troque pela senha que você deu ao usuário `flowa`):

```bash
export POSTGRES_PASSWORD=<senha do usuário flowa>
ASPNETCORE_HTTP_PORTS=8081 Fix__AcceptorPort=9876 \
ConnectionStrings__Flowa="Host=localhost;Port=5432;Database=flowa;Username=flowa;Password=$POSTGRES_PASSWORD" \
dotnet run --no-launch-profile --project src/OrderAccumulator
```

E o OrderGenerator, em outro terminal:

```bash
ASPNETCORE_HTTP_PORTS=8080 Fix__AcceptorHost=localhost Fix__AcceptorPort=9876 \
OrderAccumulator__BaseUrl=http://localhost:8081 \
dotnet run --no-launch-profile --project src/OrderGenerator
```

A página abre em http://localhost:8080. O `--no-launch-profile` faz o app usar as portas das
variáveis em vez das do `launchSettings.json`. Os dois apps gravam o commit do build e mostram em
`/version`, então precisam ser compilados dentro de um clone do git.

## Como testar

Os testes do .NET precisam do .NET 10 SDK e do Docker de pé, porque os do OrderAccumulator sobem um
PostgreSQL de verdade com Testcontainers:

```bash
dotnet build Flowa.sln
dotnet test Flowa.sln --filter "Category!=Integration"
```

Os testes de integração sobem o compose inteiro em projetos separados (portas 18080 e 18093). Eles
conferem a ida e volta FIX entre os containers e a religação depois de recriar o OrderAccumulator. Um
deles clona o repositório, então também precisa do `git`:

```bash
dotnet test tests/Flowa.IntegrationTests/Flowa.IntegrationTests.csproj
```

Na tela, dentro de `frontend/`: `npm test` roda os testes de unidade. Para o teste de ponta a ponta,
deixe a aplicação de pé em http://localhost:8080 (com `docker compose up`, por exemplo) e rode
`npx playwright install chromium` uma vez, depois `npm run e2e`.

## Decisões

**Quem inicia a sessão FIX.** O OrderGenerator é o initiator e o OrderAccumulator é o acceptor. Quem
decide fica parado esperando, e quem manda a ordem é quem liga. Se o OrderAccumulator cair, o
OrderGenerator tenta religar a cada 2 segundos e responde erro de comunicação para a tela enquanto
isso, sem travar.

**A regra do limite.** A exposição de um ativo é a soma de preço × quantidade das compras aceitas menos
a das vendas aceitas, então pode ficar negativa. Uma ordem só é aceita se a exposição depois dela
ficar, em valor absoluto, até R$ 100.000.000,00. A borda exata é aceita: o enunciado fala em não passar
do limite, e chegar a 100 milhões não passa. Um centavo acima é rejeitado. As duas bordas têm teste
(`tests/OrderAccumulator.Tests/ExposureRulesTests.cs`).

**Concorrência resolvida no banco.** A exposição só muda num `UPDATE` que testa o limite na própria
cláusula `WHERE` (`src/OrderAccumulator/Persistence/PostgresOrderProcessor.cs`). Se a ordem não couber,
nenhuma linha muda e ela é rejeitada. Como o PostgreSQL trava a linha durante o `UPDATE`, duas ordens
ao mesmo tempo no mesmo ativo não conseguem passar juntas do limite. Isso não depende de lock em
memória e continuaria valendo com mais de uma instância. Há um teste com 200 ordens simultâneas,
repetido cinco vezes.

**PostgreSQL.** A exposição precisa sobreviver a um reinício do OrderAccumulator, e o banco já resolve
a concorrência do jeito acima. As tabelas são criadas na subida, com `IF NOT EXISTS`.

**Ordem repetida.** O `ClOrdID` de cada ordem é único no banco. Se a mesma `NewOrderSingle` chegar duas
vezes, a segunda não mexe na exposição: o OrderAccumulator devolve o mesmo `ExecutionReport` da
primeira vez, com o mesmo resultado e o mesmo motivo.

**Uma regra de campo só.** Símbolo, lado, quantidade e preço são validados pelo mesmo código
(`src/Flowa.Shared`) nas duas pontas: o OrderGenerator recusa antes de mandar, e o OrderAccumulator
confere de novo o que chega pelo FIX.

## Limitações conhecidas

- Não há login nem autenticação, nem nas rotas HTTP nem na sessão FIX. A sessão é reconhecida só pelos
  nomes `ORDERGENERATOR` e `ORDERACCUMULATOR`.
- Os ativos são fixos (PETR4, VALE3 e VIIA4) e o limite é uma constante no código, não configuração.
- A ordem não é executada: ela só é aceita ou rejeitada. Não existe casamento de ordens nem preenchimento.
- As mensagens FIX ficam em memória e a numeração recomeça a cada logon. Uma mensagem perdida numa queda
  não é reenviada; o OrderGenerator responde erro de comunicação depois de 5 segundos.
- A proteção contra ordem repetida vale para o `ClOrdID` no FIX. Se a tela enviar a mesma ordem de novo,
  ela ganha um `ClOrdID` novo e conta como outra ordem.
- Não há migrações versionadas do banco: o esquema é criado na subida do OrderAccumulator.
- O CI do GitHub só compila e testa `Flowa.Shared`. Os testes dos dois apps, os de integração e os da
  tela rodam na máquina, com os comandos de "Como testar".

## Como funciona

![Desenho da arquitetura local](docs/arquitetura/arquitetura-local.png)

A tela é servida pelo próprio OrderGenerator. Quando você envia uma ordem, a tela chama
`POST /api/orders`. O OrderGenerator confere os campos e, se estiverem certos, manda uma
`NewOrderSingle` (35=D) por FIX para o OrderAccumulator. O OrderAccumulator aplica a regra do limite no
PostgreSQL e responde com um `ExecutionReport` (35=8): `New` quando aceita, `Rejected` com o motivo
quando não aceita. O OrderGenerator devolve essa resposta para a tela.

O painel de exposição chama `GET /api/exposures` no OrderGenerator, que só repassa a pergunta para o
OrderAccumulator. A fonte do desenho fica em `docs/arquitetura/arquitetura-local.drawio` e o contrato
entre as partes (rotas, mensagens FIX, portas) em `docs/contracts/contracts.md`.

This is a challenge by [Coodesh](https://coodesh.com/)
