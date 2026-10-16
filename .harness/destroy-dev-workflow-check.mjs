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
const destroyJobNames = [...(destroyWorkflow.match(/^jobs:\n((?:[ #].*\n|\n)*)/m)?.[1] ?? '').matchAll(/^ {2}([A-Za-z0-9_-]+):/gm)]
  .map((jobMatch) => jobMatch[1]);
if (destroyJobNames.join(',') !== 'destroy') {
  failures.push(`${destroyWorkflowPath}: jobs are [${destroyJobNames.join(', ')}], expected only "destroy", so that the guard covers all of them`);
}
if (!/^ {4}if: github\.ref == 'refs\/heads\/develop' && inputs\.confirmation == 'destruir'$/m.test(destroyWorkflow)) {
  failures.push(`${destroyWorkflowPath}: the destroy job has no guard "if: github.ref == 'refs/heads/develop' && inputs.confirmation == 'destruir'"`);
}
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

// Only two raw calls are allowed: the one inside the helper and the state read, whose error goes to a file and
// through the ARN filter (proved below by running it). Step names, comments and echo texts are not calls.
const allowedRawTerraformCalls = [
  'if ! terraform "$terraform_subcommand" -no-color "$@" > "$TERRAFORM_LOG" 2>&1; then',
  'if ! state_resources=$(terraform state list 2> "$state_list_errors"); then',
];
const terraformCallsOutsideTheQuietHelper = destroyWorkflow
  .split('\n')
  .map((workflowLine) => workflowLine.trim())
  .filter((workflowLine) => !/^(#|- name:|name:|echo )/.test(workflowLine))
  .filter((workflowLine) => /(^|[\s$(|;&])terraform\s+["$a-z]/.test(workflowLine))
  .filter((workflowLine) => !workflowLine.includes('run_terraform_quietly') && !allowedRawTerraformCalls.includes(workflowLine));
for (const terraformCall of terraformCallsOutsideTheQuietHelper) {
  failures.push(`${destroyWorkflowPath}: "${terraformCall}" runs outside run_terraform_quietly and its error would reach the public log`);
}
for (const allowedRawTerraformCall of allowedRawTerraformCalls) {
  if (!destroyWorkflow.includes(allowedRawTerraformCall)) failures.push(`${destroyWorkflowPath}: missing ${allowedRawTerraformCall}`);
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

const destroyPlanAndApplyStep = destroyWorkflow.match(/- name: terraform plan -destroy and apply\n((?: {8}.*\n|\n)*)/)?.[1] ?? '';
const destroyPlanPosition = destroyPlanAndApplyStep.indexOf('run_terraform_quietly plan -destroy -out=tfplan');
const destroyApplyPosition = destroyPlanAndApplyStep.indexOf('run_terraform_quietly apply tfplan');
if (destroyPlanPosition < 0 || destroyApplyPosition < destroyPlanPosition) {
  failures.push(`${destroyWorkflowPath}: the step "terraform plan -destroy and apply" does not apply the same tfplan after the plan -destroy`);
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
    'state read fails': {
      fakeTerraform: "terraform() { echo 'Error: AccessDenied for arn:aws:iam::123456789012:role/flowa-gha' >&2; return 1; }",
      expectedSuccess: false,
      forbiddenOutput: 'arn:aws',
      expectedOutput: '<hidden arn>',
    },
  };
  const stepScriptFolder = mkdtempSync(join(tmpdir(), 'destroy-dev-step-'));
  try {
    for (const [stateCaseName, { fakeTerraform, expectedSuccess, forbiddenOutput, expectedOutput }] of Object.entries(fakeTerraformByStateCase)) {
      const stepScriptPath = join(stepScriptFolder, 'step.sh');
      writeFileSync(stepScriptPath, `RUNNER_TEMP='${stepScriptFolder.replace(/\\/g, '/')}'\n${fakeTerraform}\n${emptyStateStepScript}`);
      // Same flags as the shell: bash of the job on the runner.
      const stepRun = spawnSync('bash', ['--noprofile', '--norc', '-eo', 'pipefail', stepScriptPath.replace(/\\/g, '/')], { encoding: 'utf8' });
      if (stepRun.error) {
        failures.push(`could not run bash for the case "${stateCaseName}": ${stepRun.error.message}`);
      } else if ((stepRun.status === 0) !== expectedSuccess) {
        failures.push(`last step with ${stateCaseName}: exit ${stepRun.status}, expected ${expectedSuccess ? 'success' : 'failure'}`);
      }
      const stepOutput = `${stepRun.stdout ?? ''}${stepRun.stderr ?? ''}`;
      if (forbiddenOutput && stepOutput.includes(forbiddenOutput)) {
        failures.push(`last step with ${stateCaseName}: the log shows "${forbiddenOutput}"`);
      }
      if (expectedOutput && !stepOutput.includes(expectedOutput)) {
        failures.push(`last step with ${stateCaseName}: the log does not show "${expectedOutput}"`);
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
