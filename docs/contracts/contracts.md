# Contrato entre as partes do Flowa — v2

Este arquivo é o acordo entre o OrderGenerator, o OrderAccumulator, a tela e o `docker compose`.
Quem implementa segue o que está aqui. Mudou alguma coisa? Sobe a versão e avisa quem usa.
A v2 trocou o formato das respostas HTTP: sucesso em `DataMessage` e erro em `application/problem+json`.

A regra de campo da ordem (símbolos `PETR4`/`VALE3`/`VIIA4`, lado, quantidade inteira maior que zero e
menor que 100.000, preço maior que zero, menor que 1.000 e múltiplo de 0,01) e as mensagens dela moram
só no OrderAccumulator, em `Base.OrderAccumulator.Domain.Orders` (`OrderFieldRule`, `OrderFieldMessages`).
O OrderGenerator não aplica essa regra: confere só o formato (o que nem cabe numa `NewOrderSingle`) e
manda o resto pelo FIX. A tela guarda uma cópia da regra só para avisar antes de enviar.

## 1. Rotas HTTP

JSON com nomes de campo em inglês, no formato camelCase. Mensagens para quem usa, em português.
Números (quantidade, preço, exposição) vão como número JSON, com ponto como separador decimal.

### Formato de toda resposta das rotas `/api/*`

**Sucesso** sai com `Content-Type: application/json` neste envelope (`DataMessage`):

```json
{ "success": true, "status": "Ok", "message": "Ordem aceita.", "data": { … }, "errors": [], "errorCode": null }
```

- `data` é o corpo da rota, descrito em cada seção abaixo.
- `message` é o texto em português que a tela pode mostrar.
- `status` vem pelo nome (`"Ok"`), nunca por número.

**Erro** sai com `Content-Type: application/problem+json` (RFC 9457), para qualquer cliente, inclusive
quem pede `text/html`:

```json
{
  "type": "urn:base-investimentos:problem:order-accumulator-unavailable",
  "title": "Serviço indisponível",
  "status": 503,
  "detail": "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.",
  "instance": "/api/exposures",
  "traceId": "4bf92f3577b34da6a3ce929d0e0e4736",
  "success": false,
  "statusResultado": "ServiceUnavailable",
  "errors": []
}
```

| Campo | Regra |
|---|---|
| `type` | `urn:base-investimentos:problem:<código>`. O código é fixo e é por ele que o cliente decide, nunca pelo texto. |
| `title` | Fixo por tipo de erro, em português. |
| `status` | O mesmo número do status HTTP. |
| `detail` | Mensagem em português desta ocorrência. Nunca leva exceção, SQL, host ou stack trace. |
| `instance` | Caminho pedido. |
| `traceId` | 32 caracteres hexadecimais: o trace da requisição no Datadog, o mesmo da linha de log do erro. Num erro de `POST /api/orders` depois de a ordem ter `ClOrdID` (503 e 500 da tabela da ordem), é o próprio `ClOrdID`. Sem o tracer ligado (teste local), é o `ClOrdID` no erro da ordem e o trace id da requisição no ASP.NET nos outros. |
| `success` | Sempre `false`. |
| `statusResultado` | Nome do resultado: `InvalidInput` (400), `NotFound` (404), `ServiceUnavailable` (503), `InternalError` (500). |
| `errors` | Lista de mensagens em português (no 400 de formato, uma por campo). |

Códigos de `type` usados hoje:

| Código | HTTP | Quando |
|---|---|---|
| `invalid-order` | 400 | `POST /api/orders` com ordem que não cabe no FIX |
| `invalid-page` | 400 | `GET /api/orders` com página inválida |
| `not-found` | 404 | caminho `/api/...` que não existe |
| `method-not-allowed` | 405 | verbo que a rota do OrderAccumulator não tem |
| `fix-session-not-logged-on` | 503 | `POST /api/orders` sem sessão FIX logada |
| `execution-report-timeout` | 503 | `POST /api/orders` sem `ExecutionReport` em 5 s |
| `order-accumulator-unavailable` | 503 | rota repassada sem resposta do OrderAccumulator |
| `internal-error` | 500 | erro inesperado; `detail` = `"Aconteceu um erro inesperado. Informe o traceId ao suporte."` |

Todo erro HTTP deixa exatamente uma linha de log (Warning para o erro esperado, Error com a exceção para o
inesperado), com o mesmo trace id do `traceId`, o `type` no campo `ErrorCode`, o verbo e o modelo da rota.

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
- O `ClOrdID` é gerado pelo OrderGenerator: é o trace id de 128 bits do rastro do pedido (32 caracteres
  hexadecimais). Sem tracer ligado, é um identificador aleatório de 32 caracteres. A tela não manda.

Respostas:

| Situação | HTTP | Corpo |
|---|---|---|
| Ordem aceita (`150=0`) | 200 | `DataMessage` com `message: "Ordem aceita."` e `data: { "status": "accepted", "clOrdId", "orderId", "execId", "symbol", "side", "quantity", "price" }` |
| Ordem rejeitada (`150=8`), por limite ou por campo fora da regra | 200 | `DataMessage` com `message: "<texto da tag 58>"` e `data: { "status": "rejected", … }` (mesmos campos) |
| Ordem que não cabe no FIX (nada é enviado por FIX) | 400 | problem `invalid-order`, `detail: "A ordem tem campos inválidos."`, `errors: ["Informe o preço."]` |
| Sem sessão FIX | 503 | problem `fix-session-not-logged-on`, `detail: "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes."`, `traceId` = `ClOrdID` |
| Sem resposta em 5 s | 503 | problem `execution-report-timeout`, mesmo `detail`, `traceId` = `ClOrdID` |
| `ExecutionReport` fora do contrato | 500 | problem `internal-error` (sem stack trace), `traceId` = `ClOrdID` |
| Erro inesperado antes de a ordem sair | 500 | problem `internal-error` (sem stack trace) |

- O `400` só sai quando a ordem não cabe no FIX: campo faltando (`"Informe o símbolo."`,
  `"Informe o lado da ordem."`, `"Informe a quantidade."`, `"Informe o preço."`), lado diferente de
  `"buy"`/`"sell"` (`"Lado inválido. Use compra ou venda."`), quantidade que não é número
  (`"A quantidade deve ser um número inteiro."`), preço que não é número (`"O preço deve ser um número."`)
  e símbolo com caractere de controle (`"O símbolo não pode ter caractere de controle."`). Número com mais
  dígitos do que o decimal do FIX guarda conta como "não é número". Corpo que não é JSON volta com as
  quatro mensagens de campo faltando.
- `errors` traz no máximo uma mensagem por campo, na ordem `symbol`, `side`, `quantity`, `price`.
- Campo que cabe no FIX, mas está fora da regra (símbolo `ITUB4`, quantidade `0`, `1.5` ou `100000`,
  preço `1000` ou `10.005`), vai pelo FIX e volta `200` com `data.status: "rejected"` e os motivos na `message`.
- Em `accepted` e `rejected`, `side` volta como `"buy"`/`"sell"` e `quantity`/`price` como número.

### OrderAccumulator — `GET /api/exposures`

Devolve a exposição atual dos três símbolos, sempre nesta ordem: `PETR4`, `VALE3`, `VIIA4`.
`message` é `"Exposição dos símbolos lida."` e `data` é:

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

Repassa o `GET /api/exposures` do OrderAccumulator: o mesmo `data` e a mesma `message`, num envelope só
(o `data` do OrderGenerator nunca tem outro `data` dentro).
Se o OrderAccumulator não responder em 5 s, estiver fora do ar ou responder outro status que não `200`,
devolve `503` com o problem `order-accumulator-unavailable`.

### OrderAccumulator — `GET /api/orders?page=<n>` e `DELETE /api/orders`

`GET` lista as ordens gravadas no banco, 10 por página, da mais nova para a mais velha.
`message` é `"Página de ordens lida."` e `data` é:

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
- Página `0`, negativa, texto, repetida ou acima de `1000` → `400` com o problem `invalid-page`,
  `detail: "Página inválida."` e `errors: ["A página deve ser um número inteiro de 1 a 1000."]`.
- Página além da última → `200` com `orders: []` e o `total` real.

`DELETE` apaga todas as ordens e zera a exposição de `PETR4`, `VALE3` e `VIIA4` numa transação só
(tudo ou nada) e responde `204` sem corpo (não há corpo para pôr no envelope). Não pede senha e não libera CORS.

### OrderGenerator — `GET /api/orders?page=<n>` e `DELETE /api/orders`

Repassam as duas rotas acima para o OrderAccumulator, com o mesmo prazo de 5 s do `GET /api/exposures`.

- `GET` leva só o `page`, sem mudar. No `200` devolve o mesmo `data` e a mesma `message`; no `400` devolve
  o mesmo problem `invalid-page` (com o `traceId` da requisição no OrderGenerator).
- `DELETE` devolve o `204` sem corpo. Outro verbo em `/api/orders` (fora `POST`, a ordem) não apaga nada.
- OrderAccumulator fora do ar, sem resposta em 5 s ou com outro status (`GET` diferente de `200`/`400`,
  `DELETE` diferente de `204`) → `503` com o problem `order-accumulator-unavailable`.
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
o `503` `fix-session-not-logged-on` na hora. Com sessão, espera o `ExecutionReport` do mesmo `ClOrdID` por até
5 s; passou disso, o `503` `execution-report-timeout`. Quando o OrderAccumulator volta, o initiator reloga
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
