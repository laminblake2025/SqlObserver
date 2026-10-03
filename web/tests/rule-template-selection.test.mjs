import test from 'node:test';
import assert from 'node:assert/strict';
import { readRoute } from '../src/dashboardModel.ts';
import { readRuleTemplateSelection, ruleTemplateSelectionHref } from '../src/features/alerts/ruleTemplateSelection.ts';
const id='11111111-1111-4111-8111-111111111111', job='22222222-2222-4222-8222-222222222222';
test('shared template route ignores copied diagnostic target database and time context',()=> {
  assert.deepEqual(readRoute('#/rule-templates?target='+id+'&databaseId=7&range=custom'),{page:'rule-templates',target:''});
  assert.equal(ruleTemplateSelectionHref('#/rule-templates?target='+id+'&range=1h',{templateId:id,templateVersion:2}),'#/rule-templates?templateId='+id+'&templateVersion=2');
});
test('template versions and deployment IDs restore independently and canonically',()=> {
  const h=ruleTemplateSelectionHref(ruleTemplateSelectionHref('#/rule-templates',{templateId:id.toUpperCase(),templateVersion:2}),{deploymentId:job.toUpperCase()});
  assert.deepEqual(readRuleTemplateSelection(h),{templateId:id,templateVersion:2,deploymentId:job});
  assert.deepEqual(readRuleTemplateSelection('#/rule-templates'),{});
});
test('malformed duplicate incomplete and unsafe exact selections never substitute latest',()=> {
  for(const query of ['templateId='+id,'templateVersion=1','templateId=&templateVersion=1','templateId='+id+'&templateVersion=01','templateId='+id+'&templateVersion=9007199254740992','templateId='+id+'&templateId='+id+'&templateVersion=1','templateId='+id+'&templateVersion=1&templateVersion=1'])
    assert.deepEqual(readRuleTemplateSelection('#/rule-templates?'+query),{templateId:null,templateVersion:null});
  for(const query of ['deploymentId=','deploymentId=00000000-0000-0000-0000-000000000000','deploymentId='+job+'&deploymentId='+job])
    assert.equal(readRuleTemplateSelection('#/rule-templates?'+query).deploymentId,null);
});
test('navigation keeps an unrelated invalid exact selection visible and rejects ambiguous commands',()=> {
  const h=ruleTemplateSelectionHref('#/rule-templates?templateId=bad&templateVersion=1',{deploymentId:job});
  assert.deepEqual(readRuleTemplateSelection(h),{templateId:null,templateVersion:null,deploymentId:job});
  assert.throws(()=>ruleTemplateSelectionHref('#/rules',{deploymentId:job}));
  assert.throws(()=>ruleTemplateSelectionHref('#/rule-templates',{templateId:id}));
  assert.throws(()=>ruleTemplateSelectionHref('#/rule-templates',{templateId:id,templateVersion:0}));
  assert.throws(()=>ruleTemplateSelectionHref('#/rule-templates',{deploymentId:'bad'}));
});
