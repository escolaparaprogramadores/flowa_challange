-- Tabelas do OrderAccumulator. Roda a cada subida do app, então tudo é IF NOT EXISTS.

-- Exposição por símbolo: compras aceitas menos vendas aceitas, em preço × quantidade.
CREATE TABLE IF NOT EXISTS exposures (
    symbol   text    PRIMARY KEY,
    exposure numeric NOT NULL DEFAULT 0
);

-- Cada ordem recebida, aceita ou rejeitada, com a resposta que foi dada a ela.
-- cl_ord_id é único: a mesma ordem repetida devolve a resposta gravada aqui.
CREATE TABLE IF NOT EXISTS orders (
    id            bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    cl_ord_id     text        NOT NULL CONSTRAINT orders_cl_ord_id_key UNIQUE,
    order_id      text        NOT NULL,
    exec_id       text        NOT NULL,
    symbol        text,
    side          text        NOT NULL,
    quantity      numeric     NOT NULL,
    price         numeric     NOT NULL,
    accepted      boolean     NOT NULL,
    reject_reason text,
    received_at   timestamptz NOT NULL DEFAULT now()
);

-- A lista da tela vem da ordem mais nova para a mais antiga (GET /api/orders). Sem CONCURRENTLY porque
-- este script roda dentro da transação da migração.
CREATE INDEX IF NOT EXISTS orders_received_at_id_idx ON orders (received_at DESC, id DESC);
