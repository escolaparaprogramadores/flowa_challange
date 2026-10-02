variable "region" {
  description = "Região da conta do Flowa. A policy da esteira nega qualquer outra."
  type        = string
  default     = "us-east-1"

  validation {
    condition     = var.region == "us-east-1"
    error_message = "O Flowa roda só em us-east-1."
  }
}

variable "image_tag" {
  description = "Tag das imagens no ECR (o SHA do commit). A esteira passa em todo apply."
  type        = string
  # Sem default útil: o null deixa o `apply -target` dos ECR rodar antes de existir imagem, e qualquer
  # task definition que use a tag sem ela ser passada falha alto no plan.
  default = null

  validation {
    condition     = var.image_tag == null || length(trimspace(coalesce(var.image_tag, " "))) > 0
    error_message = "image_tag não pode ser texto vazio."
  }
}

locals {
  prefixo_dos_recursos_flowa  = "flowa-challenge"
  ambiente_dos_recursos_flowa = "dev"

  # Um repositório de imagem e um log group por app, com o mesmo nome.
  nomes_dos_servicos_flowa = {
    generator   = "${local.prefixo_dos_recursos_flowa}-order-generator"
    accumulator = "${local.prefixo_dos_recursos_flowa}-order-accumulator"
  }

  # Banco e usuário iguais aos do compose (docs/contracts/contracts.md, seção 4).
  banco_nome    = "flowa"
  banco_usuario = "flowa"
  banco_porta   = 5432

  porta_fix  = 9876
  porta_http = 8081
}
