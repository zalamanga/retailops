import { useQuery } from '@tanstack/react-query';
import { all, api } from './api';
import type { Product, Supplier, Warehouse } from './types';

export function useReferences(includeArchived = false) {
  const products = useQuery({
    queryKey: ['product-options', includeArchived],
    queryFn: () => all<Product>(`/products?includeArchived=${includeArchived}`),
  });
  const suppliers = useQuery({
    queryKey: ['supplier-options'],
    queryFn: () => all<Supplier>('/suppliers'),
  });
  const warehouses = useQuery({
    queryKey: ['warehouses'],
    queryFn: () => api<Warehouse[]>('/warehouses'),
  });
  return {
    products: products.data ?? [],
    suppliers: suppliers.data ?? [],
    warehouses: warehouses.data ?? [],
    loading: products.isLoading || suppliers.isLoading || warehouses.isLoading,
    error: products.error || suppliers.error || warehouses.error,
    retry: () => {
      void products.refetch();
      void suppliers.refetch();
      void warehouses.refetch();
    },
  };
}
