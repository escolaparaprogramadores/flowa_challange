# Painel do Datadog, separado da infra da AWS: chave do Datadog com problema não trava o deploy do app.
# O state fica no mesmo bucket do infra-aws/, com chave e lock próprios; bucket e chave vêm do
# workflow (-backend-config), porque o nome do bucket não aparece no repositório público.
terraform {
  required_version = ">= 1.8.0"

  required_providers {
    datadog = {
      source  = "DataDog/datadog"
      version = "4.24.0"
    }
  }

  backend "s3" {}
}

# DD_API_KEY e DD_APP_KEY chegam por variável de ambiente, vindas dos secrets do environment dev.
# A conta é US1, o site padrão do provider; por isso não há api_url aqui.
provider "datadog" {}
