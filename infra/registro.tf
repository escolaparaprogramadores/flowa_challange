# Repositórios das imagens e log groups dos dois apps.

resource "aws_ecr_repository" "imagens_dos_servicos_flowa" {
  for_each = local.nomes_dos_servicos_flowa

  name = each.value

  # Mutável para a esteira poder rodar de novo no mesmo commit sem falhar no push.
  image_tag_mutability = "MUTABLE"

  # Ambiente do desafio: destruir leva as imagens junto, em vez de travar.
  force_delete = true

  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_lifecycle_policy" "limpeza_das_imagens_dos_servicos_flowa" {
  for_each = aws_ecr_repository.imagens_dos_servicos_flowa

  repository = each.value.name
  policy = jsonencode({
    rules = [
      {
        rulePriority = 1
        description  = "Imagem sem tag sai em 1 dia"
        selection = {
          tagStatus   = "untagged"
          countType   = "sinceImagePushed"
          countUnit   = "days"
          countNumber = 1
        }
        action = { type = "expire" }
      },
      {
        rulePriority = 2
        description  = "Guarda so as 5 ultimas imagens"
        selection = {
          tagStatus   = "any"
          countType   = "imageCountMoreThan"
          countNumber = 5
        }
        action = { type = "expire" }
      },
    ]
  })
}

# Um dia de retenção (R-04): o padrão da AWS guarda para sempre, e isso cobra.
resource "aws_cloudwatch_log_group" "logs_dos_servicos_flowa" {
  for_each = local.nomes_dos_servicos_flowa

  name              = each.value
  retention_in_days = 1
}
