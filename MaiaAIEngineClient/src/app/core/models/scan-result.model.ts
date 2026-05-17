export interface ScanResult {
  jobName:           string;
  scanType:          number;
  failuresDetected:  number;
  classifications:   number;
  recommendations:   number;
  fixesExecuted:     number;
  detail:            string;
}
