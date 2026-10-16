// Regression check of .github/workflows/9-destroy-dev.yml, run from the repository root.
// It holds the destroy to the deploy (same pins, Terraform, state key and queue), keeps it manual and guarded,
// passes every image tag to the plan (a null one breaks the refresh) and runs the last step for real with a
// fake terraform, so that a leftover resource or a failed read of the state fails the run.
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

const destroyWorkflowPath = '.github/workflows/9-destroy-dev.yml';
const deployWorkflowPath = '.github/workflows/2-develop.yml';
// The checkout on Windows turns the line ends into CRLF.
const destroyWorkflow = readFileSync(destroyWorkflowPath, 'utf8').replace(/\r\n/g, '\n');
const deployWorkflow = readFileSync(deployWorkflowPath, 'utf8').replace(/\r\n/g, '\n');
const failures = [];

function requireInDestroyWorkflow(expectedText, reason) {
  if (!destroyWorkflow.includes(expectedText)) failures.push(`${destroyWorkflowPath}: missing ${expectedText} (${reason})`);
}

const destroyTriggerBlock = destroyWorkflow.match(/^on:\n((?:[ #].*\n|\n)*)/m)?.[1] ?? '';
const destroyTriggerNames = [...destroyTriggerBlock.matchAll(/^ {2}([a-z_]+):/gm)].map((triggerMatch) => triggerMatch[1]);
if (destroyTriggerNames.join(',') !== 'workflow_dispatch') {
  failures.push(`${destroyWorkflowPath}: triggers are [${destroyTriggerNames.join(', ')}], expected only workflow_dispatch`);
}
requireInDestroyWorkflow("    if: github.ref == 'refs/heads/develop' && inputs.confirmation == 'destruir'\n", 'only from develop and with the word typed');
requireInDestroyWorkflow('    concurrency:\n      group: deploy-dev\n      cancel-in-progress: false\n', 'same queue as the deploy, never cancelled');
if (!deployWorkflow.includes('      group: deploy-dev\n')) failures.push(`${deployWorkflowPath}: the deploy queue is no longer deploy-dev`);
requireInDestroyWorkflow('          terraform_wrapper: false\n', 'the wrapper would echo the whole output in the public log');
requireInDestroyWorkflow('          mask-aws-account-id: true\n', 'the account id must not show in the public log');
requireInDestroyWorkflow('    defaults:\n      run:\n        shell: bash\n', 'pipefail in every step');

const destroyActionPins = [...destroyWorkflow.matchAll(/uses: (\S+@\S+)/g)].map((pinMatch) => pinMatch[1]);
if (destroyActionPins.length === 0) failures.push(`${destroyWorkflowPath}: no pinned action found`);
for (const destroyActionPin of destroyActionPins) {
  if (!deployWorkflow.includes(`uses: ${destroyActionPin}`)) failures.push(`${destroyWorkflowPath}: ${destroyActionPin} differs from the deploy`);
}

const terraformVersionOf = (workflowText) => workflowText.match(/terraform_version: (\S+)/)?.[1];
if (!terraformVersionOf(destroyWorkflow) || terraformVersionOf(destroyWorkflow) !== terraformVersionOf(deployWorkflow)) {
  failures.push(`terraform_version ${terraformVersionOf(destroyWorkflow)} in the destroy, ${terraformVersionOf(deployWorkflow)} in the deploy`);
}

const backendConfigsOf = (workflowText) => [...workflowText.matchAll(/-backend-config="[^"]+"/g)].map((configMatch) => configMatch[0]);
const destroyBackendConfigs = backendConfigsOf(destroyWorkflow);
const deployBackendConfigs = backendConfigsOf(deployWorkflow);
if (destroyBackendConfigs.length === 0 || destroyBackendConfigs.join(' ') !== deployBackendConfigs.join(' ')) {
  failures.push(`state backend differs: destroy [${destroyBackendConfigs.join(' ')}], deploy [${deployBackendConfigs.join(' ')}]`);
}

const terraformCallsOutsideTheQuietHelper = destroyWorkflow
  .split('\n')
  .filter((workflowLine) => /^\s*terraform (init|plan|apply|destroy|refresh|import)\b/.test(workflowLine));
for (const terraformCall of terraformCallsOutsideTheQuietHelper) {
  failures.push(`${destroyWorkflowPath}: "${terraformCall.trim()}" runs outside run_terraform_quietly and would print to the public log`);
}

const declaredImageTagVariables = readdirSync('infra-aws')
  .filter((terraformFileName) => terraformFileName.endsWith('.tf'))
  .flatMap((terraformFileName) => {
    const terraformSource = readFileSync(join('infra-aws', terraformFileName), 'utf8');
    return [...terraformSource.matchAll(/^variable "([a-z_]+_image_tag)"/gm)].map((variableMatch) => variableMatch[1]);
  });
if (declaredImageTagVariables.length === 0) {
  failures.push('no *_image_tag variable declared in infra-aws/*.tf: the check would pass empty');
}
const destroyPlanCommand = destroyWorkflow.match(/run_terraform_quietly plan -destroy(?:[^\n]*\\\n)*[^\n]*/)?.[0] ?? '';
if (!destroyPlanCommand) failures.push(`${destroyWorkflowPath}: no "run_terraform_quietly plan -destroy" command`);
for (const imageTagVariable of declaredImageTagVariables) {
  if (!destroyPlanCommand.includes(`-var "${imageTagVariable}=$GITHUB_SHA"`)) {
    failures.push(`${destroyWorkflowPath}: plan -destroy does not pass -var "${imageTagVariable}=$GITHUB_SHA"`);
  }
}

const emptyStateStepScript = destroyWorkflow
  .match(/- name: Nothing left in the state\n(?: {8}.*\n)*? {8}run: \|\n((?: {10}.*\n|\n)*)/)?.[1]
  ?.replace(/^ {10}/gm, '');
if (!emptyStateStepScript) {
  failures.push(`${destroyWorkflowPath}: no "Nothing left in the state" step with a run block`);
} else {
  const fakeTerraformByStateCase = {
    'empty state': { fakeTerraform: 'terraform() { return 0; }', expectedSuccess: true },
    'only data sources left': { fakeTerraform: "terraform() { echo 'data.aws_caller_identity.conta'; }", expectedSuccess: true },
    'a resource left': { fakeTerraform: "terraform() { printf 'data.aws_region.atual\\naws_db_instance.banco\\n'; }", expectedSuccess: false },
    'state read fails': { fakeTerraform: "terraform() { echo 'Error: Failed to load state' >&2; return 1; }", expectedSuccess: false },
  };
  const stepScriptFolder = mkdtempSync(join(tmpdir(), 'destroy-dev-step-'));
  try {
    for (const [stateCaseName, { fakeTerraform, expectedSuccess }] of Object.entries(fakeTerraformByStateCase)) {
      const stepScriptPath = join(stepScriptFolder, 'step.sh');
      writeFileSync(stepScriptPath, `${fakeTerraform}\n${emptyStateStepScript}`);
      // Same flags as the shell: bash of the job on the runner.
      const stepRun = spawnSync('bash', ['--noprofile', '--norc', '-eo', 'pipefail', stepScriptPath.replace(/\\/g, '/')], { encoding: 'utf8' });
      if (stepRun.error) {
        failures.push(`could not run bash for the case "${stateCaseName}": ${stepRun.error.message}`);
      } else if ((stepRun.status === 0) !== expectedSuccess) {
        failures.push(`last step with ${stateCaseName}: exit ${stepRun.status}, expected ${expectedSuccess ? 'success' : 'failure'}`);
      }
    }
  } finally {
    rmSync(stepScriptFolder, { recursive: true, force: true });
  }
}

if (failures.length > 0) {
  console.error(failures.join('\n'));
  process.exit(1);
}
console.log(
  `ok: manual and guarded, same queue, pins (${destroyActionPins.length}), Terraform and state as the deploy; ` +
  `plan -destroy passes ${declaredImageTagVariables.length} image tags (${declaredImageTagVariables.join(', ')}); ` +
  'last step: empty and data-only pass, leftover and failed read fail',
);
