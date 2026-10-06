CREATE TABLE IF NOT EXISTS exposures (
    symbol   text    PRIMARY KEY,
    exposure numeric NOT NULL DEFAULT 0
);

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

CREATE INDEX IF NOT EXISTS orders_received_at_id_idx ON orders (received_at DESC, id DESC);
