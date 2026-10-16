// Regression check of .github/workflows/9-destroy-dev.yml, run from the repository root.
// The refresh of a plan -destroy breaks on any image tag variable left null, so every one of them declared
// in infra-aws/ has to be passed to the plan; and a failed read of the state must not look like an empty state.
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const destroyWorkflowPath = '.github/workflows/9-destroy-dev.yml';
const destroyWorkflow = readFileSync(destroyWorkflowPath, 'utf8');
const failures = [];

const nullImageTagVariables = readdirSync('infra-aws')
  .filter((terraformFileName) => terraformFileName.endsWith('.tf'))
  .flatMap((terraformFileName) => {
    const terraformSource = readFileSync(join('infra-aws', terraformFileName), 'utf8');
    return [...terraformSource.matchAll(/variable "([a-z_]+_image_tag)" \{[^}]*?default\s*=\s*null/gs)]
      .map((variableMatch) => variableMatch[1]);
  });

if (nullImageTagVariables.length === 0) {
  failures.push('no image tag variable with default null found in infra-aws/*.tf: the check would pass empty');
}

const destroyPlanCommand = destroyWorkflow.match(/run_terraform_quietly plan -destroy[^\n]*(?:\\\n[^\n]*)*/)?.[0] ?? '';
if (!destroyPlanCommand) {
  failures.push(`${destroyWorkflowPath}: no "run_terraform_quietly plan -destroy" command`);
}
for (const imageTagVariable of nullImageTagVariables) {
  if (!destroyPlanCommand.includes(`-var "${imageTagVariable}=$GITHUB_SHA"`)) {
    failures.push(`${destroyWorkflowPath}: plan -destroy does not pass -var "${imageTagVariable}=$GITHUB_SHA"`);
  }
}

if (/terraform state list\s*\|/.test(destroyWorkflow)) {
  failures.push(`${destroyWorkflowPath}: "terraform state list" is piped, so a failed read of the state looks empty`);
}
if (!/state_resources=\$\(terraform state list\)/.test(destroyWorkflow)) {
  failures.push(`${destroyWorkflowPath}: "terraform state list" is not read on its own line`);
}

if (failures.length > 0) {
  console.error(failures.join('\n'));
  process.exit(1);
}
console.log(`ok: plan -destroy passes ${nullImageTagVariables.length} image tags (${nullImageTagVariables.join(', ')}); state list read on its own`);
