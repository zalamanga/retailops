export type Role = 'Admin' | 'Manager' | 'Operator' | 'Viewer' | 'Cashier';
export interface User {
  id: string;
  fullName: string;
  email: string;
  role: Role;
  organizationName: string;
  organizationId?: string;
}
export interface TeamUser {
  id: string;
  fullName: string;
  email: string;
  role: Role;
  isActive: boolean;
}
export interface PageResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}
export interface Product {
  id: string;
  sku: string;
  name: string;
  unit: string;
  reorderPoint: number;
  standardCost: number;
  isActive: boolean;
}
export interface Supplier {
  id: string;
  name: string;
  email: string;
  phone: string;
  leadTimeDays: number;
}
export interface Warehouse {
  id: string;
  code: string;
  name: string;
}
export type OrderStatus =
  | 'Draft'
  | 'PendingApproval'
  | 'Approved'
  | 'PartiallyReceived'
  | 'Received'
  | 'Rejected'
  | 'Cancelled';
export interface OrderSummary {
  id: string;
  number: string;
  supplierName: string;
  warehouseName: string;
  status: OrderStatus;
  total: number;
  expectedDate: string;
  createdAt: string;
  createdByName: string;
}
export interface OrderLine {
  id: string;
  productId: string;
  sku: string;
  productName: string;
  quantity: number;
  receivedQuantity: number;
  unitCost: number;
  lineTotal: number;
}
export interface Receipt {
  id: string;
  number: string;
  createdAt: string;
  createdByName: string;
  notes: string;
  lines: { orderLineId: string; quantity: number }[];
}
export interface Order extends OrderSummary {
  supplierId: string;
  warehouseId: string;
  createdById: string;
  notes: string;
  decisionReason: string | null;
  lines: OrderLine[];
  receipts: Receipt[];
}
export interface Position {
  productId: string;
  warehouseId: string;
  sku: string;
  productName: string;
  unit: string;
  warehouseName: string;
  onHand: number;
  reorderPoint: number;
  standardCost: number;
  stockValue: number;
  isLowStock: boolean;
}
export interface Movement {
  id: string;
  sku: string;
  productName: string;
  warehouseName: string;
  quantity: number;
  type: string;
  reason: string;
  reference: string;
  createdAt: string;
  actorName: string;
}
export interface Activity {
  id: string;
  actorName: string;
  action: string;
  entityType: string;
  entityId: string;
  description: string;
  createdAt: string;
}
export interface Dashboard {
  activeProducts: number;
  stockValue: number;
  lowStockCount: number;
  openOrders: number;
  movementSeries: { date: string; incoming: number; outgoing: number }[];
  lowStock: Position[];
  recentOrders: OrderSummary[];
}
export const roles: Role[] = ['Admin', 'Manager', 'Operator', 'Viewer', 'Cashier'];
export const statuses: OrderStatus[] = [
  'Draft',
  'PendingApproval',
  'Approved',
  'PartiallyReceived',
  'Received',
  'Rejected',
  'Cancelled',
];
