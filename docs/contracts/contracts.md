# Contrato entre as partes do Flowa — v1

Este arquivo é o acordo entre o OrderGenerator, o OrderAccumulator, a tela e o `docker compose`.
Quem implementa segue o que está aqui. Mudou alguma coisa? Sobe a versão e avisa quem usa.

A regra de campo da ordem (símbolos `PETR4`/`VALE3`/`VIIA4`, lado, quantidade inteira maior que zero e
menor que 100.000, preço maior que zero, menor que 1.000 e múltiplo de 0,01) e as mensagens dela moram
só no OrderAccumulator, em `Base.OrderAccumulator.Domain.Orders` (`OrderFieldRule`, `OrderFieldMessages`).
O OrderGenerator não aplica essa regra: confere só o formato (o que nem cabe numa `NewOrderSingle`) e
manda o resto pelo FIX. A tela guarda uma cópia da regra só para avisar antes de enviar.

## 1. Rotas HTTP

JSON com nomes de campo em inglês, no formato camelCase. Mensagens para quem usa, em português.
Números (quantidade, preço, exposição) vão como número JSON, com ponto como separador decimal.

### OrderGenerator — `POST /api/orders`

Recebe a ordem da tela, confere o formato, manda a `NewOrderSingle` e devolve o que o
OrderAccumulator respondeu.

Pedido:

```json
{ "symbol": "PETR4", "side": "buy", "quantity": 100, "price": 10.50 }
```

- `side` é `"buy"` (Compra) ou `"sell"` (Venda).
- O servidor lê `quantity` e `price` como texto cru (número ou string JSON) e confere o formato desse
  texto. Assim, `"abc"` na quantidade volta com a mensagem certa, e não com um erro genérico do
  framework. `1.5` na quantidade cabe no FIX: vai para o OrderAccumulator, que a rejeita.
- O `ClOrdID` é gerado pelo OrderGenerator (UUID sem traços, 32 caracteres). A tela não manda.

Respostas:

| Situação | HTTP | Corpo |
|---|---|---|
| Ordem aceita (`150=0`) | 200 | `{ "status": "accepted", "clOrdId", "orderId", "execId", "symbol", "side", "quantity", "price", "message": "Ordem aceita." }` |
| Ordem rejeitada (`150=8`), por limite ou por campo fora da regra | 200 | `{ "status": "rejected", "clOrdId", "orderId", "execId", "symbol", "side", "quantity", "price", "message": "<texto da tag 58>" }` |
| Ordem que não cabe no FIX (nada é enviado por FIX) | 400 | `{ "status": "validation_error", "message": "A ordem tem campos inválidos.", "errors": [ { "field": "price", "message": "Informe o preço." } ] }` |
| Sem sessão FIX ou sem resposta em 5 s | 503 | `{ "status": "communication_error", "message": "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes." }` |
| Erro inesperado | 500 | `{ "status": "error", "message": "Erro inesperado ao processar a ordem." }` (sem stack trace) |

- O `400` só sai quando a ordem não cabe no FIX: campo faltando (`"Informe o símbolo."`,
  `"Informe o lado da ordem."`, `"Informe a quantidade."`, `"Informe o preço."`), lado diferente de
  `"buy"`/`"sell"` (`"Lado inválido. Use compra ou venda."`), quantidade que não é número
  (`"A quantidade deve ser um número inteiro."`), preço que não é número (`"O preço deve ser um número."`)
  e símbolo com caractere de controle (`"O símbolo não pode ter caractere de controle."`). Número com mais
  dígitos do que o decimal do FIX guarda conta como "não é número".
- `field` é um de `symbol`, `side`, `quantity`, `price`, e vem no máximo um erro por campo.
- Campo que cabe no FIX, mas está fora da regra (símbolo `ITUB4`, quantidade `0`, `1.5` ou `100000`,
  preço `1000` ou `10.005`), vai pelo FIX e volta `200` com `status: "rejected"` e os motivos na `message`.
- Em `accepted` e `rejected`, `side` volta como `"buy"`/`"sell"` e `quantity`/`price` como número.

### OrderAccumulator — `GET /api/exposures`

Devolve a exposição atual dos três símbolos, sempre nesta ordem: `PETR4`, `VALE3`, `VIIA4`.

```json
{
  "limit": 100000000.00,
  "exposures": [
    { "symbol": "PETR4", "exposure": 1000.00, "remaining": 99999000.00 },
    { "symbol": "VALE3", "exposure": -500.00, "remaining": 99999500.00 },
    { "symbol": "VIIA4", "exposure": 0.00, "remaining": 100000000.00 }
  ]
}
```

- `exposure` = soma de `preço × quantidade` das compras aceitas menos a das vendas aceitas.
- `remaining` = `limit - |exposure|`: quanto ainda cabe antes de estourar, para qualquer lado.
- `limit` é a constante do OrderAccumulator. Não vem de configuração nem de variável de ambiente.

### OrderGenerator — `GET /api/exposures`

Repassa o `GET /api/exposures` do OrderAccumulator, com o mesmo corpo e o mesmo status 200.
Se o OrderAccumulator não responder em 5 s ou estiver fora do ar, devolve `503` com o mesmo corpo
`communication_error` da ordem, trocando a mensagem por
`"Não foi possível ler a exposição no OrderAccumulator. Tente de novo em instantes."`.

### OrderAccumulator — `GET /api/orders?page=<n>` e `DELETE /api/orders`

`GET` lista as ordens gravadas no banco, 10 por página, da mais nova para a mais velha:

```json
{
  "page": 1,
  "pageSize": 10,
  "total": 12,
  "orders": [
    { "receivedAt": "2026-10-04T12:00:00Z", "status": "accepted", "symbol": "PETR4", "side": "buy",
      "quantity": 100, "price": 10.50, "orderId": "…", "clOrdId": "…" }
  ]
}
```

- `receivedAt` é ISO-8601 em UTC. `status` é `"accepted"` ou `"rejected"`; `symbol` e `side` podem ser
  `null` numa ordem rejeitada que chegou pelo FIX com o campo fora do padrão.
- O tamanho da página é fixo no servidor; `pageSize` vindo do cliente é ignorado. Sem `page`, vem a página 1.
- Página `0`, negativa, texto, repetida ou acima de `1000` → `400` com
  `{ "status": "validation_error", "message": "Página inválida.", "errors": [ { "field": "page", "message": "A página deve ser um número inteiro de 1 a 1000." } ] }`.
- Página além da última → `200` com `orders: []` e o `total` real.

`DELETE` apaga todas as ordens e zera a exposição de `PETR4`, `VALE3` e `VIIA4` numa transação só
(tudo ou nada) e responde `204` sem corpo. Não pede senha e não libera CORS.

### OrderGenerator — `GET /api/orders?page=<n>` e `DELETE /api/orders`

Repassam as duas rotas acima para o OrderAccumulator, com o mesmo prazo de 5 s do `GET /api/exposures`.

- `GET` leva só o `page`, sem mudar, e devolve o `200` ou o `400` do OrderAccumulator com o mesmo corpo.
- `DELETE` devolve o `204` sem corpo. Outro verbo em `/api/orders` (fora `POST`, a ordem) não apaga nada.
- OrderAccumulator fora do ar, sem resposta em 5 s ou com outro status → `503` com o corpo
  `communication_error` e a mensagem
  `"Não foi possível ler as ordens no OrderAccumulator. Tente de novo em instantes."` (listar) ou
  `"Não foi possível apagar as ordens no OrderAccumulator. Tente de novo em instantes."` (apagar).
- Nenhuma das duas manda `Access-Control-Allow-Origin`: outra página não consegue chamá-las.

### Os dois apps — `GET /health` e `GET /version`

`/health` responde `200` com o texto `Healthy` quando o processo está de pé, sem depender da sessão
FIX nem do banco. `/version` responde `200` com `{"commit":"<sha completo, 40 caracteres>"}`: o commit
do código que está rodando, gravado no build, para conferir que a versão no ar é a que foi revisada.

## 2. Mensagens FIX 4.4

Pacotes: `QuickFIXn.Core` e `QuickFIXn.FIX44`, versão `1.14.1` (os dois têm alvo `net10.0`).

### `NewOrderSingle` (`35=D`), do OrderGenerator para o OrderAccumulator

| Tag | Campo | Valor |
|---|---|---|
| 11 | ClOrdID | gerado pelo OrderGenerator, único por ordem |
| 55 | Symbol | `PETR4`, `VALE3` ou `VIIA4` |
| 54 | Side | `1` compra, `2` venda |
| 38 | OrderQty | inteiro, `0 < q < 100000` |
| 44 | Price | `0 < p < 1000`, passo de `0.01` |
| 40 | OrdType | sempre `2` (limitada) |
| 60 | TransactTime | hora UTC do envio |

### `ExecutionReport` (`35=8`), do OrderAccumulator para o OrderGenerator

| Tag | Campo | Aceita | Rejeitada |
|---|---|---|---|
| 37 | OrderID | gerado pelo OrderAccumulator | gerado também |
| 17 | ExecID | único por relatório | único por relatório |
| 11 | ClOrdID | o da ordem | o da ordem |
| 55 | Symbol | o da ordem | o da ordem |
| 54 | Side | o da ordem | o da ordem |
| 150 | ExecType | `0` (New) | `8` (Rejected) |
| 39 | OrdStatus | `0` (New) | `8` (Rejected) |
| 151 | LeavesQty | a quantidade da ordem | `0` |
| 14 | CumQty | `0` | `0` |
| 6 | AvgPx | `0` | `0` |
| 58 | Text | não vai | o motivo, em português |

O motivo da rejeição em `58`:

- campo fora da regra (só o OrderAccumulator valida, venha a ordem da tela ou de FIX direto): as mensagens de `OrderFieldMessages` dos campos com erro, separadas por espaço,
  na ordem `symbol`, `side`, `quantity`, `price`. Uma ordem rejeitada aqui não muda a exposição.
- limite: `Ordem rejeitada: a exposição de <SÍMBOLO> passaria do limite de 100.000.000,00.`

Ordem repetida (mesmo `ClOrdID`): o OrderAccumulator devolve o `ExecutionReport` original que
gravou (mesmos `37`, `17`, `150`, `39`, `58`) e não conta a ordem de novo.

## 3. Sessão FIX

| Item | OrderGenerator | OrderAccumulator |
|---|---|---|
| Papel | initiator | acceptor |
| BeginString | `FIX.4.4` | `FIX.4.4` |
| SenderCompID | `ORDERGENERATOR` | `ORDERACCUMULATOR` |
| TargetCompID | `ORDERACCUMULATOR` | `ORDERGENERATOR` |
| Endereço | conecta em `Fix__AcceptorHost`:`Fix__AcceptorPort` | escuta em `Fix__AcceptorPort` |
| HeartBtInt | 30 | — (usa o do initiator) |
| ReconnectInterval | 2 s | — |

Nas duas pontas, para a sessão sobreviver à troca de container:

- `ResetOnLogon=Y`, `ResetOnLogout=Y`, `ResetOnDisconnect=Y`;
- store de mensagens em memória, nada em disco;
- log FIX no `stdout`, para aparecer no `docker compose logs`.

O OrderGenerator confere se a sessão está logada antes de enviar. Sem sessão, responde
`communication_error` na hora. Com sessão, espera o `ExecutionReport` do mesmo `ClOrdID` por até
5 s; passou disso, `communication_error`. Quando o OrderAccumulator volta, o initiator reloga
sozinho.

## 4. Portas e variáveis de ambiente

| Processo | Porta | Para quê |
|---|---|---|
| OrderGenerator | 8080 (HTTP); 8443 (HTTPS, só fora do compose) | página, `/api/*`, `/health`, `/version` |
| OrderAccumulator | 8081 (HTTP); 8444 (HTTPS, só fora do compose) | `GET /api/exposures`, `GET /api/orders`, `DELETE /api/orders`, `/health`, `/version` |
| OrderAccumulator | 9876 (TCP) | acceptor FIX |
| PostgreSQL | 5432 | banco do OrderAccumulator |

As variáveis seguem o padrão do ASP.NET Core (`__` separa as seções). O valor da coluna "fora do
compose" é o padrão para rodar na máquina, sem Docker.

| Variável | App | Fora do compose | No compose |
|---|---|---|---|
| `ASPNETCORE_HTTP_PORTS` | OrderGenerator | `8080` | `8080` |
| `ASPNETCORE_HTTP_PORTS` | OrderAccumulator | `8081` | `8081` |
| `ASPNETCORE_HTTPS_PORTS` | OrderGenerator / OrderAccumulator | `8443` / `8444`, certificado de desenvolvimento do .NET (`dotnet dev-certs https`) | não usa |
| `Fix__AcceptorHost` | OrderGenerator | `localhost` | `orderaccumulator` |
| `Fix__AcceptorPort` | os dois | `9876` | `9876` |
| `OrderAccumulator__BaseUrl` | OrderGenerator | `http://localhost:8081` | `http://orderaccumulator:8081` |
| `ConnectionStrings__Flowa` | OrderAccumulator | host `localhost` | host `postgres` |

A string do banco tem o formato do Npgsql: `Host=<host>;Port=5432;Database=flowa;Username=flowa;`
seguido da senha. A senha nunca fica escrita no código nem neste contrato: o compose lê de
`POSTGRES_PASSWORD` e monta a string; fora do compose, quem roda define `ConnectionStrings__Flowa`
inteira. O valor de desenvolvimento local fica declarado no README.

Nomes dos serviços no compose: `ordergenerator`, `orderaccumulator`, `postgres`. Para o avaliador,
basta publicar a porta `8080` do OrderGenerator; as outras podem ficar só na rede interna.

## 5. A página

- O build do Vite (fatia da tela) sai em `src/app-base-order-generator-webapi-ecs/wwwroot/`. Essa pasta é gerada e fica
  fora do Git.
- O OrderGenerator serve essa pasta na raiz (`/`), com `index.html` como página padrão. Caminhos que
  começam com `/api` nunca caem no `index.html`.
- Rodando o Vite em modo de desenvolvimento, ele repassa `/api` para `http://localhost:8080`.
