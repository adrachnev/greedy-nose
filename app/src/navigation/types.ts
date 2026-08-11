export type OnboardingStackParamList = {
  ConnectBank: undefined;
  ConnectConsent: undefined;
  Syncing: undefined;
  ClassifyDebitors: undefined;
};

export type TransactionsStackParamList = {
  TransactionList: undefined;
  TransactionDetail: { transactionId: string };
};

export type MainTabParamList = {
  Transactions: undefined; // nested TransactionsStackParamList
  Rules: undefined;
  Settings: undefined;
};
