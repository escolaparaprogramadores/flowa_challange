# Módulo raiz da nuvem do Flowa. O backend fica vazio de propósito: a esteira passa bucket, chave e
# região no `terraform init -backend-config=...`, e nada da conta aparece neste repositório público.
terraform {
  # 1.10 é o piso do `use_lockfile` no backend S3 (trava sem DynamoDB).
  required_version = ">= 1.10.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }

  backend "s3" {}
}

provider "aws" {
  region = var.region

  default_tags {
    tags = {
      Project   = local.prefixo
      ManagedBy = "terraform"
    }
  }
}
