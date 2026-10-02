# Banco do OrderAccumulator: a menor instância, uma AZ, sem acesso público, nas subnets isoladas.
# A senha nasce aqui e vai para o Secrets Manager; a task lê de lá pelo `valueFrom`, nunca do código.

# Só letras e números: `;` e `=` quebrariam a connection string do Npgsql, e o RDS recusa `/`, `@`, `"`
# e espaço na senha.
resource "random_password" "banco" {
  length  = 32
  special = false
}

resource "aws_db_subnet_group" "banco" {
  name        = "${local.prefixo}-banco"
  description = "Subnets isoladas do banco do Flowa"
  subnet_ids  = aws_subnet.banco[*].id
}

resource "aws_db_instance" "banco" {
  identifier = "${local.prefixo}-banco"

  engine         = "postgres"
  engine_version = "17"
  instance_class = "db.t4g.micro"
  multi_az       = false

  allocated_storage = 20
  storage_type      = "gp3"
  storage_encrypted = true

  db_name  = local.banco_nome
  username = local.banco_usuario
  password = random_password.banco.result
  port     = local.banco_porta

  db_subnet_group_name   = aws_db_subnet_group.banco.name
  vpc_security_group_ids = [aws_security_group.banco.id]
  publicly_accessible    = false

  backup_retention_period = 1

  # Ambiente do desafio: destruir não deixa snapshot cobrando nem trava por proteção.
  skip_final_snapshot = true
  deletion_protection = false

  performance_insights_enabled = false
  monitoring_interval          = 0

  auto_minor_version_upgrade = true
  apply_immediately          = true
}

resource "aws_secretsmanager_secret" "banco" {
  name        = "${local.prefixo}/${local.ambiente}/banco"
  description = "Credenciais do PostgreSQL do Flowa"

  # Sem janela de recuperação: recriar o ambiente com o mesmo nome não fica preso por 7 a 30 dias.
  recovery_window_in_days = 0
}

# `connection_string` é a chave que a task injeta como ConnectionStrings__Flowa.
resource "aws_secretsmanager_secret_version" "banco" {
  secret_id = aws_secretsmanager_secret.banco.id
  secret_string = jsonencode({
    username          = local.banco_usuario
    password          = "${random_password.banco.result}"
    host              = aws_db_instance.banco.address
    port              = local.banco_porta
    dbname            = local.banco_nome
    connection_string = "Host=${aws_db_instance.banco.address};Port=${local.banco_porta};Database=${local.banco_nome};Username=${local.banco_usuario};Password=${random_password.banco.result};SSL Mode=Require"
  })
}
