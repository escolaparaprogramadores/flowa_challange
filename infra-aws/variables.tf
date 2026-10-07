variable "region" {
  description = "Região da conta do Flowa. A policy da esteira nega qualquer outra."
  type        = string
  default     = "us-east-1"

  validation {
    condition     = var.region == "us-east-1"
    error_message = "O Flowa roda só em us-east-1."
  }
}

# Uma tag por serviço (CA-O24): o SHA do último commit que mudou o que a imagem daquele serviço copia.
# A esteira passa as duas em todo apply; o serviço que não mudou recebe a tag que já roda, e a task dele
# não é trocada.
variable "generator_image_tag" {
  description = "Tag da imagem do OrderGenerator no ECR (SHA completo do commit que a construiu)."
  type        = string
  # Sem default útil: o null deixa o `apply -target` dos ECR rodar antes de existir imagem, e a task
  # definition que use a tag sem ela ser passada falha alto no plan.
  default = null

  validation {
    condition     = var.generator_image_tag == null || can(regex("^[0-9a-f]{40}$", coalesce(var.generator_image_tag, "-")))
    error_message = "generator_image_tag tem de ser o SHA completo do commit (40 caracteres hexadecimais)."
  }
}

variable "accumulator_image_tag" {
  description = "Tag da imagem do OrderAccumulator no ECR (SHA completo do commit que a construiu)."
  type        = string
  default     = null

  validation {
    condition     = var.accumulator_image_tag == null || can(regex("^[0-9a-f]{40}$", coalesce(var.accumulator_image_tag, "-")))
    error_message = "accumulator_image_tag tem de ser o SHA completo do commit (40 caracteres hexadecimais)."
  }
}

variable "datadog_metrics_image_tag" {
  description = "Tag da imagem do worker de métricas do Datadog no ECR (SHA completo do commit que a construiu)."
  type        = string
  default     = null

  validation {
    condition     = var.datadog_metrics_image_tag == null || can(regex("^[0-9a-f]{40}$", coalesce(var.datadog_metrics_image_tag, "-")))
    error_message = "datadog_metrics_image_tag tem de ser o SHA completo do commit (40 caracteres hexadecimais)."
  }
}

locals {
  prefixo_dos_recursos_flowa  = "flowa-challenge"
  ambiente_dos_recursos_flowa = "dev"

  # Um repositório de imagem e um log group por app, com o mesmo nome.
  nomes_dos_servicos_flowa = {
    generator       = "${local.prefixo_dos_recursos_flowa}-order-generator"
    accumulator     = "${local.prefixo_dos_recursos_flowa}-order-accumulator"
    datadog_metrics = "${local.prefixo_dos_recursos_flowa}-datadog-metrics"
  }

  # Banco e usuário iguais aos do compose (docs/contracts/contracts.md, seção 4).
  banco_nome    = "flowa"
  banco_usuario = "flowa"
  banco_porta   = 5432

  # Teto de conexões do Generator no banco. A db.t3.micro aceita LEAST(DBInstanceClassMemory/9531392, 5000)
  # conexões (perto de 80 com 1 GiB, 3 delas reservadas ao superusuário); 10 por task deixa folga para o
  # Accumulator, o worker de métricas e as duas tasks que convivem durante o deploy.
  limite_do_pool_do_generator = 10

  # O mesmo teto no Accumulator: a decisão da ordem segura a conexão só durante a transação curta, e 10 + 10
  # por task (o dobro durante o deploy) ainda deixa folga para o worker de métricas na db.t3.micro.
  limite_do_pool_do_accumulator = 10

  # O worker de métricas faz uma leitura de cada vez a cada 5 minutos: 2 conexões bastam.
  limite_do_pool_do_datadog_metrics = 2

  porta_fix = 9876
}
