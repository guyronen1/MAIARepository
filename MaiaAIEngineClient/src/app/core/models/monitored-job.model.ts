export interface ScanCheckRule {
  checkRuleId:      number;
  checkType:        string;
  sourceTable:      string | null;
  targetField:      string;
  minValue:         number | null;
  maxValue:         number | null;
  expectedValue:    string | null;
  watermarkColumn:  string | null;
  sourceIdColumn:   string | null;
  severity:         string;
  description:      string | null;
}

export interface RuleOverride {
  ruleId:        number;
  pattern:       string;
  errorTypeCode: string;
  confidence:    number;
  priority:      number;
}

export interface MonitoredJob {
  monitoredJobId:          number;
  name:                    string;
  displayName:             string | null;
  jobTypeName:             string;
  scanTypeId:              number;
  scanTypeName:            string;
  logFolder:               string | null;
  searchPatterns:          string | null;
  connectionName:          string | null;
  logSourceUrl:            string | null;
  pollingIntervalSeconds:  number;
  isActive:                boolean;
  description:             string | null;
  createdAt:               string;
  scanCheckRules:          ScanCheckRule[];
  rules:                   RuleOverride[];
}
