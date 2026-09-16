import { overviewWindow } from '../overview/overviewModel.ts';
import type { OverviewScope } from '../overview/overviewTypes';
export function analyticsWindow(scope:OverviewScope,now:number) {
  let window;
  try {window=overviewWindow(scope,now);}
  catch {throw new Error('Choose a valid UTC range of up to 7 days, ending no later than now.');}
  if(Date.parse(window.toUtc)-Date.parse(window.fromUtc)>7*86400000) throw new Error('Analytics evidence supports ranges up to 7 days. Choose a shorter range.');
  return window;
}
