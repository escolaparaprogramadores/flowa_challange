# Cofre da chave de API do Datadog (CA-O2). O Terraform cria só o cofre, vazio: o dono cola a chave no
# console, e nem a esteira consegue lê-la (a role dela tem Deny em GetSecretValue em .../dev/datadog/*).
# Quem lê o valor é a execution role das tasks, na hora em que a task sobe.
resource "aws_secretsmanager_secret" "chave_api_do_datadog" {
  name        = "${local.prefixo_dos_recursos_flowa}/${local.ambiente_dos_recursos_flowa}/datadog/api-key"
  description = "Chave de API do Datadog, em texto puro, colada pelo dono"

  # Como o segredo do banco: recriar o ambiente com o mesmo nome não fica preso por 7 a 30 dias.
  recovery_window_in_days = 0
}
